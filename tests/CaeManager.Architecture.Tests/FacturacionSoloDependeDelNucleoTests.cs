using System.Reflection;
using System.Reflection.Emit;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Regla D-F2 (análisis de módulos y facturación del Operador CAE, 2026-11-14): el módulo Facturación depende solo del
/// Núcleo. Es una lista blanca, no una lista negra: los tipos de un namespace con el segmento <c>Facturacion</c> (Domain,
/// Application, Infrastructure y Web, compilados) solo pueden referenciar tipos de la propia Facturación, de las áreas del
/// Núcleo de ADR-013 y de las bibliotecas permitidas; cualquier área nueva de Outbound o de Inbound da rojo sin tener que
/// nombrarla. Las referencias se leen de las firmas y del IL de cada método (llamadas, campos, tipos y tokens), así que
/// cuentan también los nombres cualificados, las lambdas y las máquinas de estado de los métodos asíncronos.
///
/// <para>
/// Provisional: sustituye al registro de módulos de M0a y M0b (ADR-013, «Migración por fases») mientras no exista. Cuando
/// llegue ese registro, la lista de áreas del Núcleo y la deuda congelada pasan a él y este test se retira o se convierte
/// en una vista del registro. Las áreas del Núcleo que ADR-013 da como propuesta a confirmar (Operaciones, Documentos…) se
/// aceptan aquí hasta que M0a fije el corte.
/// </para>
///
/// <para>
/// La deuda que ya existía al registrar el módulo (la pantalla «Facturación» del Operador CAE y las tarifas por Cliente
/// empresarial, que se apoyan en el Cuadro de dirección, en Clientes y en Dashboard) está congelada por tipo y área en
/// <see cref="DeudaCongelada"/>: solo puede encoger. Lo nuevo de V-2 (la marca de cobro) entra limpio: lee Outbound por el
/// puerto <see cref="IFuenteLiquidacionOperadorCae"/> del Núcleo.
/// </para>
/// </summary>
public class FacturacionSoloDependeDelNucleoTests
{
    /// <summary>Áreas del Núcleo según la tabla de clasificación de ADR-013 (Firme y Propuesta), más la propia Facturación.</summary>
    private static readonly HashSet<string> AreasPermitidas = new(StringComparer.Ordinal)
    {
        "Facturacion",
        // Infraestructura de todos, identidad y Tenant propietario.
        "Common", "DependencyInjection", "Tenants", "Usuarios", "Identity", "Autenticacion", "Seguridad", "ApiKeys", "DataProtection",
        "MultiTenancy", "Auditoria", "Auditing", "Autorizacion", "Firmas",
        // Plano de Plataforma.
        "Plataforma", "Soporte", "Telemetria",
        // Entidades comunes y plano Comercial.
        "Empresas", "Trabajadores", "Vehiculos", "Centros", "RelacionesEmpresariales", "Operaciones", "Comercial", "Cumplimiento",
        "Documentos", "TiposDocumento", "Calendario", "CalendarioLaboral",
        // Servicios técnicos.
        "Exportacion", "Persistence", "FileStorage", "Email", "Notificaciones", "Configuracion",
        // Armazón de la interfaz (Web fuera de Features): componentes, sistema de diseño, recursos y la raíz de Web.
        "Web.Components", "Web.Shared", "Web.Recursos", "Web.Infraestructura", "Web.Seguridad", "Web.Services", "Web.Exportacion", "Web",
    };

    /// <summary>Raíces de namespace de BCL y de los paquetes que Facturación puede usar.</summary>
    private static readonly string[] BibliotecasPermitidas =
    [
        "System", "Microsoft", "MediatR", "FluentValidation", "ClosedXML", "DocumentFormat", "Npgsql", "Bunit",
    ];

    /// <summary>
    /// Deuda anterior al registro del módulo, «tipo de Facturación -> área no permitida». Solo puede encoger: si una entrada
    /// ya no se mide, se quita en el mismo commit.
    /// </summary>
    private static readonly HashSet<string> DeudaCongelada = new(StringComparer.Ordinal)
    {
        // Resumen por Cliente empresarial: lee Asignaciones, Proyectos y Visitas de Outbound directamente.
        "CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion.ObtenerResumenFacturacionQueryHandler -> Asignaciones",
        "CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion.ObtenerResumenFacturacionQueryHandler -> Dashboard",
        "CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion.ObtenerResumenFacturacionQueryHandler -> Proyectos",
        "CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion.ObtenerResumenFacturacionQueryHandler -> Visitas",
        // Pantalla «Facturación» del Operador CAE: se valora con el Cuadro de dirección y TarifaOperadorCae (Dashboard).
        "CaeManager.Web.Features.Facturacion.ConceptoFacturado -> Dashboard",
        "CaeManager.Web.Features.Facturacion.EmpresaFacturada -> Dashboard",
        "CaeManager.Web.Features.Facturacion.FacturacionEndpoints -> Dashboard",
        "CaeManager.Web.Features.Facturacion.FacturacionOperadorModelo -> CuadroDireccion",
        "CaeManager.Web.Features.Facturacion.FacturacionOperadorModelo -> Dashboard",
        "CaeManager.Web.Features.Facturacion.ModeloFacturacion -> Dashboard",
        "CaeManager.Web.Features.Facturacion.Pages.Facturacion -> Dashboard",
        "CaeManager.Web.Features.Facturacion.Pages.FacturacionPorClienteEmpresarial -> Clientes",
        // V-2: el CSV escribe el ModeloFacturacion de la pantalla tal cual (sus enums EstadoCifraMensual y
        // EstadoTarifaConcepto son de Dashboard); se va con la deuda de ModeloFacturacion, no antes.
        "CaeManager.Web.Features.Facturacion.LiquidacionOperadorCsv -> Dashboard",
    };

    [Fact]
    public void Facturacion_solo_referencia_el_Nucleo_y_las_bibliotecas_permitidas_salvo_la_deuda_congelada()
    {
        var tipos = TiposDeFacturacion().ToList();
        tipos.Should().NotBeEmpty("control del instrumento: si no encuentra tipos de Facturación, el vacío no es cumplimiento");
        tipos.Select(t => t.FullName).Should().Contain(
        [
            "CaeManager.Application.Facturacion.LiquidacionesOperadorCae.MarcarCobroLiquidacionOperadorCaeCommandHandler",
            "CaeManager.Domain.Facturacion.CobroLiquidacionOperadorCae",
            "CaeManager.Web.Features.Facturacion.FacturacionEndpoints",
        ], "control del instrumento: tiene que mirar las cuatro capas de Facturación");

        var medidas = new SortedSet<string>(StringComparer.Ordinal);
        var externas = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tipo in tipos)
        {
            var raiz = Raiz(tipo);
            foreach (var referido in Referencias(tipo))
            {
                if (referido.Namespace is not { } ns)
                    continue;
                // El compilador de Razor genera tipos auxiliares de cada componente en «__Blazor.<namespace del componente>».
                if (ns.StartsWith("__Blazor.", StringComparison.Ordinal))
                    ns = ns["__Blazor.".Length..];
                if (ns.StartsWith("CaeManager.", StringComparison.Ordinal))
                {
                    var area = Area(ns);
                    if (!AreasPermitidas.Contains(area))
                        medidas.Add($"{raiz.FullName} -> {area}");
                }
                else if (!BibliotecasPermitidas.Any(b => ns == b || ns.StartsWith(b + ".", StringComparison.Ordinal)))
                {
                    externas.Add($"{raiz.FullName} -> {ns}");
                }
            }
        }

        string.Join("\n", externas).Should().BeEmpty("Facturación solo usa BCL y los paquetes de BibliotecasPermitidas");
        string.Join("\n", medidas.Except(DeudaCongelada)).Should().BeEmpty(
            "Facturación depende solo del Núcleo (D-F2): para leer Outbound, un puerto del Núcleo como IFuenteLiquidacionOperadorCae");
        string.Join("\n", DeudaCongelada.Except(medidas)).Should().BeEmpty(
            "la deuda congelada solo encoge: quita de DeudaCongelada lo que ya no se mide");
    }

    [Fact]
    public void Lo_nuevo_de_la_liquidacion_del_Operador_CAE_no_tiene_deuda()
    {
        DeudaCongelada.Where(d => d.Contains("LiquidacionesOperadorCae", StringComparison.Ordinal)
                                  || d.StartsWith("CaeManager.Domain.Facturacion.CobroLiquidacion", StringComparison.Ordinal))
            .Should().BeEmpty("la marca de cobro nació en el módulo y lee Outbound solo por el puerto del Núcleo");
    }

    private static IEnumerable<Type> TiposDeFacturacion() =>
        new[] { typeof(EntidadConTenant).Assembly, typeof(ICommand).Assembly, typeof(CaeManagerDbContext).Assembly, typeof(CaeManager.Web.Features.Facturacion.FacturacionEndpoints).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Namespace?.Split('.').Contains("Facturacion") == true);

    private static Type Raiz(Type tipo)
    {
        while (tipo.DeclaringType is { } contenedor)
            tipo = contenedor;
        return tipo;
    }

    /// <summary>
    /// El área de un namespace de TALVEG: el tercer segmento en Domain, Application e Infrastructure
    /// (<c>CaeManager.Application.Dashboard.Queries</c> -> <c>Dashboard</c>), el cuarto en <c>CaeManager.Web.Features</c> y
    /// <c>Web.&lt;segmento&gt;</c> para el resto de Web.
    /// </summary>
    private static string Area(string ns)
    {
        var s = ns.Split('.');
        if (s.Length >= 2 && s[1] == "Web")
            return s.Length >= 4 && s[2] == "Features" ? s[3] : s.Length >= 3 ? "Web." + s[2] : "Web";
        return s.Length >= 3 ? s[2] : s[^1];
    }

    private const BindingFlags Todo = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                                      | BindingFlags.DeclaredOnly;

    private static IEnumerable<Type> Referencias(Type tipo)
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

    private static readonly Dictionary<short, OpCode> CodigosDeOperacion = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

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
