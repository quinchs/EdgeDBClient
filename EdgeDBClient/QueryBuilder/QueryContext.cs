﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace EdgeDB.QueryBuilder
{
    internal class QueryContext<TInner>
    {
        public Type ParameterType { get; set; }
        public string? ParameterName { get; set; }
        public Expression? Body { get; set; }

        public QueryContext(Expression<Func<TInner, bool>> func)
        {
            Body = func.Body;
            ParameterType = func.Parameters[0].Type;
            ParameterName = func.Parameters[0].Name;
        }

        public object? GetCallerInstance(Type type)
        {
            return null;
        }
    }
}
