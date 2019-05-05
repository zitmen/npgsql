﻿using System;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Npgsql.PostgresTypes;
using Npgsql.TypeHandling;

namespace Npgsql.TypeHandlers.CompositeHandlers
{
    sealed class CompositeStructMemberHandler<TComposite, TMember> : CompositeMemberHandler<TComposite>
        where TComposite : struct
    {
        delegate TMember GetMember(ref TComposite composite);
        delegate void SetMember(ref TComposite composite, TMember value);

        readonly GetMember? _get;
        readonly SetMember? _set;
        readonly NpgsqlTypeHandler _handler;

        public CompositeStructMemberHandler(FieldInfo fieldInfo, PostgresType postgresType, NpgsqlTypeHandler handler)
            : base(fieldInfo, postgresType)
        {
            var composite = Expression.Parameter(typeof(TComposite), "composite");
            var value = Expression.Parameter(typeof(TMember), "value");

            _get = Expression
                .Lambda<GetMember>(Expression.Field(composite, fieldInfo), composite)
                .Compile();
            _set = Expression
                .Lambda<SetMember>(Expression.Assign(Expression.Field(composite, fieldInfo), value), composite, value)
                .Compile();
            _handler = handler;
        }

        public CompositeStructMemberHandler(PropertyInfo propertyInfo, PostgresType postgresType, NpgsqlTypeHandler handler)
            : base(propertyInfo, postgresType)
        {
            var getMethod = propertyInfo.GetGetMethod();
            if (getMethod != null)
                _get = (GetMember)Delegate.CreateDelegate(typeof(GetMember), getMethod);

            var setMethod = propertyInfo.GetSetMethod();
            if (setMethod != null)
                _set = (SetMember)Delegate.CreateDelegate(typeof(SetMember), setMethod);

            Debug.Assert(setMethod != null || getMethod != null);

            _handler = handler;
        }

        public override ValueTask Read(TComposite composite, NpgsqlReadBuffer buffer, bool async)
            => throw new NotSupportedException();

        public override async ValueTask Read(ByReference<TComposite> composite, NpgsqlReadBuffer buffer, bool async)
        {
            await buffer.Ensure(sizeof(uint) + sizeof(int), async);

            var oid = buffer.ReadUInt32();
            Debug.Assert(oid == PostgresType.OID);

            var length = buffer.ReadInt32();
            if (length == -1)
                return;

            var value = NullableHandler<TMember>.Exists
                ? await NullableHandler<TMember>.ReadAsync(_handler, buffer, length, async, null)
                : await _handler.Read<TMember>(buffer, length, async);

            Set(composite, value);
        }

        public override async Task Write(TComposite composite, NpgsqlWriteBuffer buffer, NpgsqlLengthCache? lengthCache, bool async)
        {
            Debug.Assert(_get != null);

            if (buffer.WriteSpaceLeft < sizeof(int))
                await buffer.Flush(async);

            buffer.WriteUInt32(PostgresType.OID);
            await (NullableHandler<TMember>.Exists
                ? NullableHandler<TMember>.WriteAsync(_handler, _get(ref composite), buffer, lengthCache, null, async)
                : _handler.WriteWithLengthInternal(_get(ref composite), buffer, lengthCache, null, async));
        }

        public override int ValidateAndGetLength(TComposite composite, ref NpgsqlLengthCache? lengthCache)
            => _get == null
                ? throw new NpgsqlException($"Can't write type '{typeof(TComposite).Name} because member {MemberInfo.Name} has no getter'")
                : NullableHandler<TMember>.Exists
                    ? NullableHandler<TMember>.ValidateAndGetLength(_handler, _get(ref composite), ref lengthCache, null)
                    : _handler.ValidateAndGetLength(_get(ref composite), ref lengthCache, null);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Set(ByReference<TComposite> composite, TMember value)
        {
            if (_set == null)
                throw new NpgsqlException($"Can't read type '{typeof(TComposite).Name} because member {MemberInfo.Name} has no setter'");
            _set(ref composite.Value, value);
        }
    }
}