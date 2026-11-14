using System.Reflection;
using System.Reflection.Emit;

namespace CaeManager.Architecture.Tests.Soporte;

/// <summary>
/// Los tipos que un tipo referencia, leídos de sus firmas y del IL de cada método (ADR-013 § 4.3, test 2). Es el
/// instrumento común de <c>FacturacionSoloDependeDelNucleoTests</c> y de <c>DependenciasEntreModulosTests</c>.
///
/// <para>
/// <b>Qué cuenta.</b> Base e interfaces, atributos (con sus argumentos de tipo), campos, propiedades, y de cada método
/// el tipo devuelto, los parámetros y los operandos de tipo, campo y método del cuerpo IL (llamadas, <c>newobj</c>,
/// <c>castclass</c>, <c>ldtoken</c>, <c>ldftn</c>…), con los argumentos genéricos expandidos. Cuentan así los nombres
/// cualificados, las lambdas y las máquinas de estado de los métodos asíncronos, que la reflexión de firmas no ve.
/// </para>
///
/// <para>
/// <b>Lo que no ve</b> (límite conocido, ADR-013 § 4.3): las constantes, que el compilador copia al IL sin referencia al
/// tipo; los tipos de variables locales que el cuerpo no toca; una petición de MediatR construida por reflexión o por
/// nombre; SQL en cadenas; una columna <c>Guid</c> sin clave foránea; un <c>cref</c> de documentación. El IL de
/// <c>Debug</c> y el de <c>Release</c> pueden diferir en el detalle: las listas se miden con la configuración por
/// defecto de <c>dotnet test</c>, la misma de CI.
/// </para>
/// </summary>
internal static class ReferenciasIl
{
    private const BindingFlags Todo = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                                      | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> CodigosDeOperacion = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .GroupBy(o => o.Value)
        .ToDictionary(g => g.Key, g => g.First());

    /// <summary>Los tipos distintos referenciados por <paramref name="tipo"/> (no los de sus tipos anidados).</summary>
    public static IEnumerable<Type> De(Type tipo)
    {
        var directas = new List<Type?>();
        directas.Add(tipo.BaseType);
        directas.AddRange(tipo.GetInterfaces());
        directas.AddRange(Atributos(tipo.CustomAttributes));
        foreach (var campo in tipo.GetFields(Todo))
        {
            directas.Add(campo.FieldType);
            directas.AddRange(Atributos(campo.CustomAttributes));
        }
        foreach (var propiedad in tipo.GetProperties(Todo))
        {
            directas.Add(propiedad.PropertyType);
            directas.AddRange(Atributos(propiedad.CustomAttributes));
        }
        foreach (var metodo in tipo.GetMethods(Todo).Cast<MethodBase>().Concat(tipo.GetConstructors(Todo)))
        {
            if (metodo is MethodInfo mi)
                directas.Add(mi.ReturnType);
            directas.AddRange(metodo.GetParameters().Select(p => p.ParameterType));
            directas.AddRange(Atributos(metodo.CustomAttributes));
            directas.AddRange(DelIl(metodo));
        }

        return directas.Where(t => t is not null).SelectMany(t => Expandir(t!)).Distinct();
    }

    private static IEnumerable<Type> Atributos(IEnumerable<CustomAttributeData> atributos)
    {
        foreach (var atributo in atributos)
        {
            yield return atributo.AttributeType;
            foreach (var argumento in atributo.ConstructorArguments.Concat(atributo.NamedArguments.Select(n => n.TypedValue)))
                if (argumento.Value is Type t)
                    yield return t;
        }
    }

    private static IEnumerable<Type> Expandir(Type tipo)
    {
        if (tipo.IsGenericParameter)
            yield break;
        if (tipo.HasElementType && tipo.GetElementType() is { } elemento)
        {
            foreach (var t in Expandir(elemento))
                yield return t;
            yield break;
        }
        yield return tipo.IsGenericType ? tipo.GetGenericTypeDefinition() : tipo;
        if (tipo.IsGenericType)
            foreach (var argumento in tipo.GetGenericArguments())
                foreach (var t in Expandir(argumento))
                    yield return t;
    }

    /// <summary>Los tipos que nombra el cuerpo del método: llamadas, campos, tipos y tokens del IL.</summary>
    private static IEnumerable<Type> DelIl(MethodBase metodo)
    {
        byte[]? il;
        try
        {
            il = metodo.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            il = null;
        }
        if (il is null)
            return [];

        var tipos = new List<Type>();
        var argumentosTipo = metodo.DeclaringType is { IsGenericType: true } d ? d.GetGenericArguments() : null;
        var argumentosMetodo = metodo.IsGenericMethod ? metodo.GetGenericArguments() : null;
        var i = 0;
        while (i < il.Length)
        {
            short valor = il[i++];
            if (valor == 0xFE && i < il.Length)
                valor = unchecked((short)(0xFE00 | il[i++]));
            if (!CodigosDeOperacion.TryGetValue(valor, out var codigo))
                break;

            switch (codigo.OperandType)
            {
                case OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok:
                    var token = BitConverter.ToInt32(il, i);
                    i += 4;
                    try
                    {
                        switch (metodo.Module.ResolveMember(token, argumentosTipo, argumentosMetodo))
                        {
                            case Type t:
                                tipos.Add(t);
                                break;
                            case FieldInfo f:
                                tipos.Add(f.DeclaringType!);
                                tipos.Add(f.FieldType);
                                break;
                            case MethodBase m:
                                if (m.DeclaringType is { } declarante)
                                    tipos.Add(declarante);
                                if (m is MethodInfo mi)
                                {
                                    tipos.Add(mi.ReturnType);
                                    if (mi.IsGenericMethod)
                                        tipos.AddRange(mi.GetGenericArguments());
                                }
                                tipos.AddRange(m.GetParameters().Select(p => p.ParameterType));
                                break;
                        }
                    }
                    catch (Exception e) when (e is ArgumentException or BadImageFormatException or TypeLoadException or FileNotFoundException)
                    {
                        // Un token que no se resuelve (p. ej. una referencia a un ensamblado opcional) no es de TALVEG.
                    }
                    break;
                case OperandType.InlineSwitch:
                    var casos = BitConverter.ToInt32(il, i);
                    i += 4 + (4 * casos);
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    i += 8;
                    break;
                default:
                    i += 4;
                    break;
            }
        }

        return tipos;
    }
}
