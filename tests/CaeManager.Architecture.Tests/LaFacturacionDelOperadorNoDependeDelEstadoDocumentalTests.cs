using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Regla de producto (PR 10 del estado materializado): lo que el Operador CAE externo cobra por Tenant beneficiario —Trabajadores
/// extranjeros con Visita, Centros y Obras nuevas, y sus tarifas— no depende del estado documental. Por eso el código que lo
/// mide no puede depender del estado materializado ni de los servicios que calculan estados: si un día lo hiciera, un cambio
/// de lectura (leer las tablas en vez de calcular) podría mover un importe. Este test congela esa frontera por las dependencias
/// que recibe cada pieza; la prueba de que ningún importe cambia con las tablas envenenadas está en
/// <c>CuadroYDashboardLeenElEstadoMaterializadoTests</c> (integración).
/// </summary>
public class LaFacturacionDelOperadorNoDependeDelEstadoDocumentalTests
{
    private static readonly HashSet<string> ServiciosDeEstado =
    [
        "ICalculoEstadoCentroService", "ICalculoEstadoDocumentalService", "ICalculoEstadoSubcontrataService",
        "IEvaluacionDeAccesoPorCentroService", "IEvaluacionDeAccesoDelTenant", "IRequisitosObligatoriosIncumplidosService",
        "IRecalculoEstadoMaterializado",
    ];

    private static IEnumerable<Type> PiezasDeFacturacion()
    {
        var application = typeof(ICommand).Assembly;
        return application.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.Namespace is not null)
            .Where(t =>
                // Toda la feature Facturación (tarifas por Cliente y resumen) …
                t.Namespace!.StartsWith("CaeManager.Application.Facturacion", StringComparison.Ordinal)
                // … y, del Cuadro de dirección, lo que mide o cobra unidades del Operador CAE.
                || (t.Namespace.StartsWith("CaeManager.Application.Dashboard", StringComparison.Ordinal)
                    && (t.Name.Contains("Facturacion", StringComparison.Ordinal)
                        || t.Name.Contains("Tarifa", StringComparison.Ordinal)
                        || t.Name.Contains("Extranjeros", StringComparison.Ordinal)
                        || t.Name.Contains("EmpresasGestionadas", StringComparison.Ordinal)
                        || t.Name.Contains("Valoracion", StringComparison.Ordinal)
                        || t.Name == nameof(MedicionMensualCuadroDireccion)
                        || t.Name == nameof(ObtenerSerieCuadroDireccionQueryHandler))));
    }

    private static bool EsDeEstado(Type tipo) =>
        tipo.Namespace == "CaeManager.Application.EstadoMaterializado"
        || tipo.Namespace?.StartsWith("CaeManager.Domain.EstadoMaterializado", StringComparison.Ordinal) == true
        || ServiciosDeEstado.Contains(tipo.Name);

    [Fact]
    public void La_facturacion_del_Operador_CAE_no_recibe_ningun_servicio_de_estado()
    {
        var piezas = PiezasDeFacturacion().ToList();
        piezas.Select(p => p.Name).Should().Contain(
            [nameof(ObtenerFacturacionOperadorCaeQueryHandler), nameof(MedicionMensualCuadroDireccion), nameof(ContarExtranjerosCobrablesDelMesQueryHandler)],
            "el test tiene que estar mirando la facturación y no una lista vacía");

        var infractores = piezas
            .SelectMany(pieza => pieza.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Select(p => p.ParameterType)
                .Concat(pieza.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Select(f => f.FieldType))
                .Where(EsDeEstado)
                .Select(tipo => $"{pieza.Name} depende de {tipo.Name}"))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        string.Join("; ", infractores).Should().BeEmpty(
            "lo que se cobra por Tenant beneficiario no depende del estado documental: no puede leerlo ni calcularlo");
    }

    [Fact]
    public void Solo_el_cumplimiento_del_Cuadro_y_del_Dashboard_ejecutivo_recibe_el_lector_del_estado()
    {
        var application = typeof(ICommand).Assembly;
        var quienLoRecibe = application.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetConstructors().SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType.Name == "ILectorCumplimientoDeDireccion"))
            .Select(t => t.Name)
            .OrderBy(x => x)
            .ToList();

        quienLoRecibe.Should().Equal(
            [nameof(ObtenerCatalogoKpisQueryHandler), nameof(ObtenerCumplimientoExigidoTenantQueryHandler)],
            "el lector del cumplimiento de dirección lo usan el cumplimiento por Tenant del Cuadro y el catálogo del Dashboard " +
            "ejecutivo; si lo recibe otra pieza hay que revisar que no sea una que cobre");
    }
}
