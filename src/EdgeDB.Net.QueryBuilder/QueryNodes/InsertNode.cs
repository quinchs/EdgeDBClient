﻿using EdgeDB.DataTypes;
using EdgeDB.Serializer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EdgeDB.QueryNodes
{
    /// <summary>
    ///    Represents a 'INSERT' node.
    /// </summary>
    internal class InsertNode : QueryNode<InsertContext>
    {
        /// <summary>
        ///     Whether or not to autogenerate the unless conflict clause.
        /// </summary>
        private bool _autogenerateUnlessConflict;

        /// <summary>
        ///     The else clause if any.
        /// </summary>
        private readonly StringBuilder _elseStatement;

        /// <summary>
        ///     The list of currently inserted types used to determine if 
        ///     a nested query can be preformed.
        /// </summary>
        private readonly List<Type> _subQueryMap = new();

        /// <inheritdoc/>
        public InsertNode(NodeBuilder builder) : base(builder) 
        {
            _elseStatement = new();
        }

        /// <summary>
        ///     Builds a json-based insert shape
        /// </summary>
        /// <returns>
        ///     The insert shape for a json-based value.
        /// </returns>
        private string BuildJsonShape()
        {
            var mappingName = QueryUtils.GenerateRandomVariableName();
            var jsonValue = (IJsonVariable)Context.Value!;
            var depth = jsonValue.Depth;

            // create a depth map that contains each nested level of types to be inserted
            IGrouping<int, DepthNode>[] depthMap = JsonUtils.BuildDepthMap(mappingName, jsonValue).ToArray().GroupBy(x => x.Depth).ToArray();
                
            // generate the global maps
            for(int i = depth; i != 0; i--)
            {
                var map = depthMap[i];
                var node = map.First();
                var iterationName = QueryUtils.GenerateRandomVariableName();
                var variableName = QueryUtils.GenerateRandomVariableName();
                var isLast = depthMap.Length == i + 1;

                // IMPORTANT: since we're using 'i' within the callback, we must
                // store a local here so we dont end up calling the last iterations 'i' value
                var indexCopy = i; 

                // generate a introspection-dependant sub query for the insert or select
                var query = new SubQuery((info) =>
                {
                    var allProps = QueryGenerationUtils.GetProperties(info, node.Type);
                    var typeName = node.Type.GetEdgeDBTypeName();

                    // define the insert shape
                    var shape = allProps.Select(x =>
                    {
                        var edgedbName = x.GetEdgeDBPropertyName();
                        
                        // if its a link, add a ternary statement for pulling the value out of a sub-map
                        if (EdgeDBTypeUtils.IsLink(x.PropertyType, out var isArray, out _))
                        {
                            // if we're in the last iteration of the depth map, we know for certian there
                            // are no sub types within the current context, we can safely set the link to 
                            // an empty set
                            if (isLast)
                                return $"{edgedbName} := {{}}";

                            // return a slice operator for multi links or a index operator for single links
                            return isArray 
                                ? $"{edgedbName} := distinct array_unpack({mappingName}_d{indexCopy + 1}[<int64>json_get({iterationName}, '{x.Name}', '{mappingName}_depth_from') ?? 0:<int64>json_get({iterationName}, '{x.Name}', '{mappingName}_depth_to') ?? 0])" 
                                : $"{edgedbName} := {mappingName}_d{indexCopy + 1}[<int64>json_get({iterationName}, '{x.Name}', '{mappingName}_depth_index')] if json_typeof(json_get({iterationName}, '{x.Name}')) != 'null' else <{x.PropertyType.GetEdgeDBTypeName()}>{{}}";
                        }

                        // if its a scalar type, use json_get to pull the value and cast it to our property
                        // type
                        if (!EdgeDBTypeUtils.TryGetScalarType(x.PropertyType, out var edgeqlType))
                            throw new NotSupportedException($"Cannot use type {x.PropertyType} as there is no serializer for it");

                        return $"{edgedbName} := <{edgeqlType}>json_get({iterationName}, '{x.Name}')";

                    });

                    // generate the 'insert .. unless conflict .. else select' query
                    var exclusiveProps = QueryGenerationUtils.GetProperties(info, node.Type, true);
                    var exclusiveCondition = exclusiveProps.Any() ?
                        $" unless conflict on {(exclusiveProps.Count() > 1 ? $"({string.Join(", ", exclusiveProps.Select(x => $".{x.GetEdgeDBPropertyName()}"))})" : $".{exclusiveProps.First().GetEdgeDBPropertyName()}")} else (select {typeName})"
                        : string.Empty;

                    var insertStatement = $"(insert {typeName} {{ {string.Join(", ", shape)} }}{exclusiveCondition})";

                    // add the iteration and turn it into an array so we can use the index operand
                    // during our query stage
                    return $"array_agg((for {iterationName} in json_array_unpack(<json>${variableName}) union {insertStatement}))";
                });

                // tell the builder that this query requires introspection
                RequiresIntrospection = true;

                // serialize this depths values and set the variable & global for the sub-query
                var iterationJson = JsonConvert.SerializeObject(map.Select(x => x.JsonNode));

                SetVariable(variableName, new Json(iterationJson));

                SetGlobal($"{mappingName}_d{i}", query, null);
            }

            // replace the json variables content with the root depth map
            SetVariable(jsonValue.VariableName, new Json(JsonConvert.SerializeObject(depthMap[0].Select(x => x.JsonNode))));

            // create the base insert shape
            var shape = jsonValue.InnerType.GetEdgeDBTargetProperties(excludeId: true).Select(x =>
            {
                var edgedbName = x.GetEdgeDBPropertyName();

                // if its a link, add a ternary statement for pulling the value out of a sub-map
                if (EdgeDBTypeUtils.IsLink(x.PropertyType, out var isArray, out _))
                {
                    // return a slice operator for multi links or a index operator for single links
                    return isArray
                               ? $"{edgedbName} := distinct array_unpack({mappingName}_d1[<int64>json_get({jsonValue.Name}, '{x.Name}', '{mappingName}_depth_from') ?? 0:<int64>json_get({jsonValue.Name}, '{x.Name}', '{mappingName}_depth_to') ?? 0])"
                               : $"{edgedbName} := {mappingName}_d1[<int64>json_get({jsonValue.Name}, '{x.Name}', '{mappingName}_depth_index')] if json_typeof(json_get({jsonValue.Name}, '{x.Name}')) != 'null' else <{x.PropertyType.GetEdgeDBTypeName()}>{{}}";
                }

                // if its a scalar type, use json_get to pull the value and cast it to our property
                // type
                if (!EdgeDBTypeUtils.TryGetScalarType(x.PropertyType, out var edgeqlType))
                    throw new NotSupportedException($"Cannot use type {x.PropertyType} as there is no serializer for it");

                return $"{edgedbName} := <{edgeqlType}>json_get({jsonValue.Name}, '{x.Name}')";
            });

            // return out our insert shape
            return $"{{ {string.Join(", ", shape)} }}";
        }

        /// <summary>
        ///     Builds an insert shape based on the given type and value.
        /// </summary>
        /// <param name="shapeType">The type to build the shape for.</param>
        /// <param name="shapeValue">The value to build the shape with.</param>
        /// <returns>The built insert shape.</returns>
        /// <exception cref="InvalidOperationException">
        ///     No serialization method could be found for a property.
        /// </exception>
        private string BuildInsertShape(Type? shapeType = null, object? shapeValue = null)
        {
            List<string> shape = new();
            
            // use the provide shape and value if they're not null, otherwise
            // use the ones defined in context
            var type = shapeType ?? OperatingType;
            var value = shapeValue ?? Context.Value;

            // if the value is an expression we can just directly translate it
            if (value is LambdaExpression expression)
                return $"{{ {ExpressionTranslator.Translate(expression, Builder.QueryVariables, Context, Builder.QueryGlobals)} }}";

            // get all properties that aren't marked with the EdgeDBIgnore attribute
            var properties = type.GetEdgeDBTargetProperties(excludeId: true);

            foreach(var property in properties)
            {
                // define the type and whether or not it's a link
                var propType = property.PropertyType;
                var isLink = EdgeDBTypeUtils.IsLink(property.PropertyType, out var isArray, out var innerType);
                
                // get the equivalent edgedb property name
                var propertyName = property.GetEdgeDBPropertyName();
                
                // if a scalar type is found for the property type
                if(EdgeDBTypeUtils.TryGetScalarType(propType, out var edgeqlType))
                {
                    // set it as a variable and continue the iteration
                    var varName = QueryUtils.GenerateRandomVariableName();
                    SetVariable(varName, property.GetValue(value));
                    shape.Add($"{propertyName} := <{edgeqlType}>${varName}");
                    continue;
                }

                // if the property is a link
                if (isLink)
                {
                    // get the value
                    var subValue = property.GetValue(value);

                    // if its null we can append an empty set
                    if (subValue is null)
                        shape.Add($"{propertyName} := {{}}");
                    else if (isArray) // if its a multi link
                    {
                        List<string> subShape = new();

                        // iterate over all values and generate their resolver
                        foreach (var item in (IEnumerable)subValue!)
                        {
                            subShape.Add(BuildLinkResolver(innerType!, item));
                        }

                        // append the sub-shape
                        shape.Add($"{propertyName} := {{ {string.Join(", ", subShape)} }}");
                    }
                    else // generate the link resolver and append it
                        shape.Add($"{propertyName} := {BuildLinkResolver(propType, subValue)}");

                    continue;
                }

                throw new InvalidOperationException($"Failed to find method to serialize the property \"{property.PropertyType.Name}\" on type {type.Name}");
            }

            return $"{{ {string.Join(", ", shape)} }}";
        }

        /// <summary>
        ///     Resolves a sub query for a link.
        /// </summary>
        /// <param name="type">The type of the link</param>
        /// <param name="value">The value of the link.</param>
        /// <returns>
        ///     A sub query or global name to reference the links value within the query.
        /// </returns>
        private string BuildLinkResolver(Type type, object? value)
        {
            // if the value is null we can just return an empty set
            if (value is null)
                return "{}";

            // is it a value thats been returned from a previous query?
            if (QueryObjectManager.TryGetObjectId(value, out var id))
            {
                // add a sub select statement
                return InlineOrGlobal(
                    type,
                    new SubQuery($"(select {type.GetEdgeDBTypeName()} filter .id = <uuid>\"{id}\")"),
                    value);
            }
            else
            {
                RequiresIntrospection = true;
             
                // add a insert select statement
                return InlineOrGlobal(type, new SubQuery((info) =>
                {
                    var name = type.GetEdgeDBTypeName();
                    var exclusiveProps = QueryGenerationUtils.GetProperties(info, type, true);
                    var exclusiveCondition = exclusiveProps.Any() ?
                        $" unless conflict on {(exclusiveProps.Count() > 1 ? $"({string.Join(", ", exclusiveProps.Select(x => $".{x.GetEdgeDBPropertyName()}"))})" : $".{exclusiveProps.First().GetEdgeDBPropertyName()}")} else (select {name})"
                        : string.Empty;
                    return $"(insert {name} {BuildInsertShape(type, value)}{exclusiveCondition})";
                }), value);
            }
        }

        /// <summary>
        ///     Adds a sub query as an inline query or as a global depending on if the current 
        ///     query contains any statements for the provided type.
        /// </summary>
        /// <param name="type">The returning type of the sub query.</param>
        /// <param name="value">The query itself.</param>
        /// <param name="reference">The optional reference object.</param>
        /// <returns>
        ///     A sub query or global name to reference the sub query.
        /// </returns>
        private string InlineOrGlobal(Type type, SubQuery value, object? reference)
        {
            // if were in a query with the type or the query requires introspection add it as a global
            if (_subQueryMap.Contains(type) || (value is SubQuery sq && sq.RequiresIntrospection))
                return GetOrAddGlobal(reference, value);

            // add it to our sub query map and return the inlined version
            _subQueryMap.Add(type);
            return value is SubQuery subQuery && subQuery.Query != null
                ? subQuery.Query
                : value.ToString()!;
        }

        /// <inheritdoc/>
        public override void Visit()
        {
            // add the current type to the sub query map
            _subQueryMap.Add(OperatingType);

            // build the insert shape
            var shape = Context.IsJsonVariable ? BuildJsonShape() : BuildInsertShape();

            // append it to our query
            Query.Append($"insert {OperatingType.GetEdgeDBTypeName()} {shape}");
        }

        /// <inheritdoc/>
        public override void FinalizeQuery()
        {
            // if we require autogeneration of the unless conflict statement
            if(_autogenerateUnlessConflict)
            {
                if (SchemaInfo is null)
                    throw new NotSupportedException("Cannot use autogenerated unless conflict on without schema interpolation");

                if (!SchemaInfo.TryGetObjectInfo(OperatingType, out var typeInfo))
                    throw new NotSupportedException($"Could not find type info for {OperatingType}");

                Query.Append($" {ConflictUtils.GenerateExclusiveConflictStatement(typeInfo, _elseStatement.Length != 0)}");
            }
            
            Query.Append(_elseStatement);

            // if the query builder wants this node as a global
            if (Context.SetAsGlobal && Context.GlobalName != null)
            {
                SetGlobal(Context.GlobalName, new SubQuery($"({Query})"), null);
                Query.Clear();
            }
        }

        /// <summary>
        ///     Adds a unless conflict on (...) statement to the insert node
        /// </summary>
        /// <remarks>
        ///     This method requires introspection on the <see cref="FinalizeQuery"/> step.
        /// </remarks>
        public void UnlessConflict()
        {
            _autogenerateUnlessConflict = true;
            RequiresIntrospection = true;
        }

        /// <summary>
        ///     Adds a unless conflict on statement to the insert node
        /// </summary>
        /// <param name="selector">The property selector for the conflict clause.</param>
        public void UnlessConflictOn(LambdaExpression selector)
        {
            Query.Append($" unless conflict on {ExpressionTranslator.Translate(selector, Builder.QueryVariables, Context, Builder.QueryGlobals)}");
        }

        /// <summary>
        ///     Adds the default else clause to the insert node that returns the conflicting object.
        /// </summary>
        public void ElseDefault()
        {
            _elseStatement.Append($" else (select {OperatingType.GetEdgeDBTypeName()})");
        }

        /// <summary>
        ///     Adds a else statement to the insert node.
        /// </summary>
        /// <param name="builder">The builder that contains the else statement.</param>
        public void Else(IQueryBuilder builder)
        {
            // remove addon & autogen nodes.
            var userNodes = builder.Nodes.Where(x => !builder.Nodes.Any(y => y.SubNodes.Contains(x)) || !x.IsAutoGenerated);

            // TODO: better checks for this, future should add a callback to add the
            // node with its context so any parent builder can change contexts for nodes
            foreach (var node in userNodes)
                node.Context.SetAsGlobal = false;

            foreach(var variable in builder.Variables)
            {
                Builder.QueryVariables[variable.Key] = variable.Value;
            }

            var newBuilder = new QueryBuilder<object?>(userNodes.ToList(), builder.Globals.ToList(), builder.Variables.ToDictionary(x => x.Key, x=> x.Value));

            var result = newBuilder.BuildWithGlobals();
            _elseStatement.Append($" else ({result.Query})");
        }
    }
}
