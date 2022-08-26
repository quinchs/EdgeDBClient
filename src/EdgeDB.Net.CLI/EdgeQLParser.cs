﻿using EdgeDB.CLI.Utils;
using EdgeDB.Codecs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EdgeDB.CLI
{
    internal class EdgeQLParser
    {
        private static readonly Regex _headerHashRegex = new(@"\/\/ edgeql:([0-9a-fA-F]{64})");

        public static async Task<GenerationResult> ParseAndGenerateAsync(EdgeDBTcpClient client, string @namespace, GenerationTargetInfo targetInfo)
        {
            var parseResult = await client.ParseAsync(targetInfo.EdgeQL!, Cardinality.Many, IOFormat.Binary, Capabilities.All, default);

            return GenerateCSharpFromEdgeQL(@namespace, targetInfo, parseResult);
        }

        public static bool TargetFileHashMatches(string header, string hash)
        {
            var match = _headerHashRegex.Match(header);
            if (!match.Success)
                return false;
            return match.Groups[1].Value == hash;
        }

        public static GenerationTargetInfo GetTargetInfo(string edgeqlFile, string targetDir)
        {
            string fileContent = File.ReadAllText(edgeqlFile);
            var hash = HashUtils.HashEdgeQL(fileContent);
            var fileName = TextUtils.ToPascalCase(Path.GetFileName(edgeqlFile).Split('.')[0]);

            return new GenerationTargetInfo
            {
                FileNameWithoutExtension = fileName,
                EdgeQL = fileContent,
                EdgeQLHash = hash,
                EdgeQLFilePath = edgeqlFile,
                TargetFilePath = Path.Combine(targetDir, $"{fileName}.g.cs")
            };
        }

        public class GenerationTargetInfo
        {
            public string? FileNameWithoutExtension { get; set; }
            public string? EdgeQLFilePath { get; set; }
            public string? TargetFilePath { get; set; }
            public string? EdgeQL { get; set; }
            public string? EdgeQLHash { get; set; }

            public bool GeneratedTargetExistsAndIsUpToDate()
            {
                var lines = File.Exists(TargetFilePath) ? File.ReadAllLines(TargetFilePath) : Array.Empty<string>();

                return File.Exists(TargetFilePath) && lines.Length >= 2 && TargetFileHashMatches(lines[1], EdgeQLHash!);
            }
        }

        public class GenerationResult
        {
            public string? Code { get; set; }
            public string? EdgeQLHash { get; set; }
            public string? ExecuterClassName { get; set; }
            public string? ReturnResult { get; set; }
            public IEnumerable<string>? Parameters { get; set; }
        }

        private static GenerationResult GenerateCSharpFromEdgeQL(string @namespace, GenerationTargetInfo targetInfo, EdgeDBBinaryClient.ParseResult parseResult)
        {
            var codecType = GetTypeOfCodec(parseResult.OutCodec.Codec, $"{targetInfo.FileNameWithoutExtension} Result");

            // create the class writer

            var writer = new CodeWriter();
            writer.AppendLine("// AUTOGENERATED: DO NOT MODIFY");
            writer.AppendLine($"// edgeql:{targetInfo.EdgeQLHash}");
            writer.AppendLine($"// Generated on {DateTime.UtcNow:O}");
            writer.AppendLine("#nullable enable");
            writer.AppendLine($"using EdgeDB;");
            writer.AppendLine();
            writer.AppendLine($"namespace {@namespace};");
            writer.AppendLine();

            var refTypes = new List<CodecTypeInfo>();
            var compliledTypes = new List<(CodecTypeInfo Info, string Reference)?>();

            var mainResult = BuildTypes(codecType, out var typeName, refTypes);

            compliledTypes.Add((codecType, mainResult));

            if (refTypes.Any() || codecType.IsObject)
            {
                var seenTypes = new HashSet<CodecTypeInfo>(refTypes) { codecType };
                var refStack = new Stack<CodecTypeInfo>(refTypes);

                writer.AppendLine("#region Types");
                writer.AppendLine(mainResult);

                // circle dependency safe!
                while (refStack.TryPop(out var typeInfo))
                {
                    var complRef = compliledTypes.FirstOrDefault(x => x!.Value.Info.BodyEquals(typeInfo));

                    if (complRef is not null)
                    {
                        writer.AppendLine(complRef.Value.Reference);
                        continue;
                    }

                    var newTypes = new List<CodecTypeInfo>();
                    var result = BuildTypes(typeInfo, out _, newTypes);

                    if (newTypes.Any())
                        foreach (var newType in newTypes.Where(x => !seenTypes.TryGetValue(x, out _)))
                            refStack.Push(newType);

                    writer.AppendLine(result);
                    compliledTypes.Add((typeInfo, result));
                }

                writer.AppendLine("#endregion");
                writer.AppendLine();
            }

            // create the executor class
            var classScope = writer.BeginScope($"public static class {targetInfo.FileNameWithoutExtension}");

            writer.AppendLine($"public static readonly string Query = @\"{targetInfo.EdgeQL}\";");
            writer.AppendLine();
            var method = parseResult.Cardinality switch
            {
                Cardinality.AtMostOne => "QuerySingleAsync",
                Cardinality.One => "QueryRequiredSingleAsync",
                _ => "QueryAsync"
            };

            var resultType = parseResult.Cardinality switch
            {
                Cardinality.AtMostOne => $"{typeName ?? mainResult}?",
                Cardinality.One => typeName ?? mainResult,
                _ => $"IReadOnlyCollection<{typeName ?? mainResult}?>"
            };

            // build args
            IEnumerable<string>? argParameters;
            IEnumerable<string>? methodArgs;
            if (parseResult.InCodec.Codec is NullCodec)
            {
                methodArgs = Array.Empty<string>();
                argParameters = Array.Empty<string>();
            }
            else if (parseResult.InCodec.Codec is Codecs.Object argCodec)
            {
                argParameters = argParameters = argCodec.PropertyNames.Select((x, i) => BuildTypes(GetTypeOfCodec(argCodec.InnerCodecs[i], x), out _, namesOnScalar: true, camelCase: true));
                methodArgs = methodArgs = argCodec.PropertyNames.Select((x, i) =>
                {
                    return $"{{ \"{x}\", {TextUtils.ToCamelCase(x)} }}";
                });
            }
            else
                throw new InvalidOperationException("Argument codec is malformed");

            writer.AppendLine($"public static Task<{resultType}> ExecuteAsync(IEdgeDBQueryable client{(argParameters.Any() ? $", {string.Join(", ", argParameters)}" : "")}, CancellationToken token = default)");
            writer.AppendLine($"    => client.{method}<{typeName ?? mainResult}>(Query{(methodArgs.Any() ? $", new Dictionary<string, object?>() {{ {string.Join(", ", methodArgs)} }}" : "")}, capabilities: (Capabilities){(ulong)parseResult.Capabilities}ul, token: token);");

            writer.AppendLine();
            writer.AppendLine($"public static Task<{resultType}> {targetInfo.FileNameWithoutExtension}Async(this IEdgeDBQueryable client{(argParameters.Any() ? $", {string.Join(", ", argParameters)}" : "")}, CancellationToken token = default)");
            writer.AppendLine($"    => ExecuteAsync(client{(argParameters.Any() ? $", {string.Join(", ", argParameters.Select(x => x.Split(' ')[1]))}" : "")}, token: token);");

            classScope.Dispose();

            writer.AppendLine("#nullable restore");

            return new()
            {
                ExecuterClassName = targetInfo.FileNameWithoutExtension,
                EdgeQLHash = targetInfo.EdgeQLHash,
                ReturnResult = resultType,
                Parameters = argParameters,
                Code = writer.ToString()
            };
        }

        private static string BuildTypes(CodecTypeInfo info, out string? resultTypeName, List<CodecTypeInfo>? usedObjects = null, bool namesOnScalar = false, bool camelCase = false, bool returnTypeName = false)
        {
            usedObjects ??= new();
            var writer = new CodeWriter();
            resultTypeName = null;

            var fmtName = info.Name is not null
                ? camelCase
                    ? TextUtils.ToCamelCase(info.Name)
                    : TextUtils.ToPascalCase(info.Name)
                : null;

            if (info.IsObject)
            {
                if (returnTypeName)
                {
                    // add to used objects
                    usedObjects.Add(info);
                    if (namesOnScalar)
                        return $"{info.GetUniqueTypeName()}? {fmtName}";
                    return fmtName!;
                }

                // create the main class
                writer.AppendLine("[EdgeDBType]");
                writer.AppendLine($"public sealed class {info.GetUniqueTypeName()}");
                using (_ = writer.BeginScope())
                {
                    var properties = info.Children!.Select(x =>
                    {
                        var result = BuildTypes(x, out _, usedObjects, namesOnScalar: true, returnTypeName: true);
                        return $"[EdgeDBProperty(\"{x.Name}\")]{Environment.NewLine}    public {result} {{ get; set; }}";
                    });

                    writer.AppendLine(string.Join($"{Environment.NewLine}{Environment.NewLine}    ", properties));
                }

                resultTypeName = info.TypeName!;
                return writer.ToString();
            }

            if (info.IsTuple)
            {
                var types = info.Children!.Select(x => BuildTypes(x, out _, usedObjects, true));
                return $"({string.Join(", ", types)}){(namesOnScalar ? $" {fmtName}" : "")}";
            }

            if (info.IsArray)
            {
                var result = BuildTypes(info.Children!.Single(), out _, usedObjects, true);
                return $"{result}[]{(namesOnScalar ? $" {fmtName}" : "")}";
            }

            if (info.IsSet)
            {
                var result = BuildTypes(info.Children!.Single(), out _, usedObjects, true);
                return $"IEnumerable<{result}>{(namesOnScalar ? $" {fmtName}" : "")}";
            }


            if (info.TypeName is not null)
                return $"{info.TypeName}{(namesOnScalar ? $" {fmtName}" : "")}";

            throw new InvalidOperationException($"Unknown type def {info}");
        }

        private static CodecTypeInfo GetTypeOfCodec(ICodec codec, string? name = null, CodecTypeInfo? parent = null)
        {
            // TODO: complete codec parsing

            CodecTypeInfo info;

            switch (codec)
            {
                case Codecs.Object obj:
                    {
                        info = new CodecTypeInfo
                        {
                            IsObject = true,
                            TypeName = TextUtils.ToPascalCase(name!)
                        };
                        info.Children = obj.InnerCodecs
                            .Select((x, i) =>
                                obj.PropertyNames[i] is "__tname__" or "__tid__"
                                    ? null
                                    : GetTypeOfCodec(x, obj.PropertyNames[i], info))
                            .Where(x => x is not null)!;
                    }
                    break;
                case ICodec set when ReflectionUtils.IsSubclassOfRawGeneric(typeof(Set<>), set.GetType()):
                    {
                        info = new CodecTypeInfo
                        {
                            IsArray = true,
                        };
                        info.Children = new[]
                        {
                        GetTypeOfCodec((ICodec)set.GetType().GetField("_innerCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(set)!, parent: info)
                    };
                    }
                    break;
                case ICodec array when ReflectionUtils.IsSubclassOfRawGeneric(typeof(Array<>), array.GetType()):
                    {
                        info = new CodecTypeInfo
                        {
                            IsSet = true,
                        };
                        info.Children = new[]
                        {
                        GetTypeOfCodec((ICodec)array.GetType().GetField("_innerCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(array)!, parent: info)
                    };
                    }
                    break;
                case Codecs.Tuple tuple:
                    {
                        info = new CodecTypeInfo
                        {
                            IsTuple = true,
                        };
                        info.Children = tuple.InnerCodecs.Select(x => GetTypeOfCodec(x, parent: info));
                    }
                    break;
                case ICodec scalar when ReflectionUtils.IsSubclassOfInterfaceGeneric(typeof(IScalarCodec<>), codec!.GetType()):
                    {
                        info = new CodecTypeInfo
                        {
                            TypeName = $"{codec.GetType().GetInterface("IScalarCodec`1")!.GetGenericArguments()[0].Name}{(codec.GetType().GetInterface("IScalarCodec`1")!.GetGenericArguments()[0].IsValueType ? "" : "?")}",
                        };
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unknown codec {codec}");
            }

            info.Name = name ?? info.Name;
            info.Parent = parent;

            return info;
        }

        private class CodecTypeInfo
        {
            public bool IsArray { get; set; }
            public bool IsSet { get; set; }
            public bool IsObject { get; set; }
            public bool IsTuple { get; set; }
            public string? Name { get; set; }
            public string? TypeName { get; set; }
            public IEnumerable<CodecTypeInfo>? Children { get; set; }
            public CodecTypeInfo? Parent { get; set; }

            public bool BodyEquals(CodecTypeInfo info)
            {
                return IsArray == info.IsArray &&
                       IsSet == info.IsSet &&
                       IsObject == info.IsObject &&
                       IsTuple == info.IsTuple &&
                       (info.Children?.SequenceEqual(Children ?? Array.Empty<CodecTypeInfo>()) ?? false);
            }

            public string GetUniqueTypeName()
            {
                List<string?> path = new() { TypeName };
                var p = Parent;
                while (p is not null)
                {
                    path.Add(p.TypeName);
                    p = p.Parent;
                }
                path.Reverse();
                return string.Join("", path.Where(x => x is not null));
            }

            public override string ToString()
            {
                return $"{Name} ({TypeName})";
            }
        }
    }
}
