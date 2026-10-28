using System.Reflection.Emit;
using System.Reflection;

namespace OpenAgent.Architecture.Tests;

/// <summary>Includes signatures and IL references, including static calls and async state machines.</summary>
internal static class TypeDependencies
{
    private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);

    internal static IReadOnlySet<Type> Read(Type type)
    {
        HashSet<Type> dependencies = [];
        Add(dependencies, type.BaseType);
        foreach (Type contract in type.GetInterfaces())
        {
            Add(dependencies, contract);
        }
        foreach (FieldInfo field in type.GetFields(DeclaredMembers))
        {
            Add(dependencies, field.FieldType);
        }
        IEnumerable<MethodBase> methods = type.GetMethods(DeclaredMembers).Cast<MethodBase>()
            .Concat(type.GetConstructors(DeclaredMembers));
        foreach (MethodBase method in methods)
        {
            AddMember(dependencies, method);
            MethodBody? body = method.GetMethodBody();
            if (body == null)
            {
                continue;
            }
            foreach (LocalVariableInfo local in body.LocalVariables)
            {
                Add(dependencies, local.LocalType);
            }
            foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
            {
                if (clause.Flags == ExceptionHandlingClauseOptions.Clause)
                {
                    Add(dependencies, clause.CatchType);
                }
            }
            ReadInstructions(dependencies, method, body.GetILAsByteArray() ?? []);
        }
        return dependencies;
    }

    private static void Add(HashSet<Type> dependencies, Type? type)
    {
        if (type == null || !dependencies.Add(type))
        {
            return;
        }
        if (type.HasElementType)
        {
            Add(dependencies, type.GetElementType());
        }
        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                Add(dependencies, argument);
            }
        }
        if (type.IsGenericParameter)
        {
            foreach (Type constraint in type.GetGenericParameterConstraints())
            {
                Add(dependencies, constraint);
            }
        }
    }

    private static void AddMember(HashSet<Type> dependencies, MemberInfo member)
    {
        Add(dependencies, member.DeclaringType);
        switch (member)
        {
            case Type type:
                Add(dependencies, type);
                break;
            case FieldInfo field:
                Add(dependencies, field.FieldType);
                break;
            case MethodBase method:
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Add(dependencies, parameter.ParameterType);
                }
                if (method is MethodInfo methodInfo)
                {
                    Add(dependencies, methodInfo.ReturnType);
                    foreach (Type argument in methodInfo.GetGenericArguments())
                    {
                        Add(dependencies, argument);
                    }
                }
                break;
        }
    }

    private static void ReadInstructions(HashSet<Type> dependencies, MethodBase method, byte[] bytes)
    {
        int position = 0;
        while (position < bytes.Length)
        {
            short value = bytes[position++];
            if (value == 0xfe)
            {
                value = (short)(0xfe00 | bytes[position++]);
            }
            OperandType operand = OpCodesByValue[value].OperandType;
            if (operand is OperandType.InlineField or OperandType.InlineMethod
                or OperandType.InlineType or OperandType.InlineTok)
            {
                int token = BitConverter.ToInt32(bytes, position);
                MemberInfo? member = method.Module.ResolveMember(
                    token,
                    method.DeclaringType?.GetGenericArguments(),
                    method is MethodInfo info ? info.GetGenericArguments() : null);
                if (member != null)
                {
                    AddMember(dependencies, member);
                }
            }
            position += operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, position),
                _ => 4
            };
        }
    }
}
