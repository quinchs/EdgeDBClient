﻿using EdgeDB.Operators;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace EdgeDB
{
    internal abstract class ExpressionTranslator<TExpression> : ExpressionTranslator
        where TExpression : Expression
    {
        public abstract string Translate(TExpression expression, ExpressionContext context);

        public override string Translate(Expression expression, ExpressionContext context)
        {
            return Translate((TExpression)expression, context);
        }
    }

    internal abstract class ExpressionTranslator
    {
        private static readonly Dictionary<Type, ExpressionTranslator> _translators = new();
        private static readonly IEdgeQLOperator[] _operators;
        private static readonly Dictionary<ExpressionType, IEdgeQLOperator> _expressionOperators;

        static ExpressionTranslator()
        {
            var types = Assembly.GetExecutingAssembly().DefinedTypes;
            // load current translators
            var translators = types.Where(x => x.BaseType?.Name == "ExpressionTranslator`1");

            foreach(var translator in translators)
            {
                _translators[translator.BaseType!.GenericTypeArguments[0]] = (ExpressionTranslator)Activator.CreateInstance(translator)!;
            }

            // load operators
            _operators = types.Where(x => x.ImplementedInterfaces.Any(x => x == typeof(IEdgeQLOperator))).Select(x => (IEdgeQLOperator)Activator.CreateInstance(x)!).ToArray();

            // set the expression operators
            _expressionOperators = _operators.Where(x => x.Expression is not null).DistinctBy(x => x.Expression).ToDictionary(x => (ExpressionType)x.Expression!, x => x);
        }

        protected static bool TryGetExpressionOperator(ExpressionType type, [MaybeNullWhen(false)] out IEdgeQLOperator edgeqlOperator)
            => _expressionOperators.TryGetValue(type, out edgeqlOperator);


        public abstract string Translate(Expression expression, ExpressionContext context);

        public static string Translate<TInnerExpression>(Expression<TInnerExpression> expression)
        {
            var context = new ExpressionContext(expression);
            return TranslateExpression(expression.Body, context);
        }

        protected static string TranslateExpression(Expression expression, ExpressionContext context)
        {
            if (_translators.TryGetValue(expression.GetType(), out var translator))
                return translator.Translate(expression, context);

            throw new Exception("AAAA");
        }

        protected static string ParseObject(object? obj)
        {
            if (obj is null)
                return "{}";

            if(obj is Enum enm)
            {
                var type = enm.GetType();
                var att = type.GetCustomAttribute<EnumSerializerAttribute>();
                return att != null ? att.Method switch
                {
                    SerializationMethod.Lower => $"\"{obj.ToString()?.ToLower()}\"",
                    SerializationMethod.Numeric => Convert.ChangeType(obj, type.BaseType ?? typeof(int)).ToString() ?? "{}",
                    _ => "{}"
                } : Convert.ChangeType(obj, type.BaseType ?? typeof(int)).ToString() ?? "{}";
            }

            return obj switch
            {
                string str => $"\"{str}\"",
                char chr => $"\"{chr}\"",
                Type type => PacketSerializer.GetEdgeQLType(type) ?? type.GetEdgeDBTypeName(),
                _ => obj.ToString()!
            };
        }
    }
}
