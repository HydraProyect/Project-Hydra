using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Regla D-F2 (análisis de módulos y facturación del Operador CAE, 2026-11-14): el módulo Facturación depende solo del
/// Núcleo. Con el registro de módulos de ADR-013 (M0a, <see cref="RegistroDeModulos"/>) los tipos de Facturación y las
/// áreas del Núcleo ya no se listan aquí: se leen del registro (<c>Modulos/Modulos-registro.txt</c>), y las referencias
/// salen del IL de cada tipo (<see cref="ReferenciasIl"/>, vía <see cref="AristasEntreModulos"/>): firmas, llamadas,
/// campos, lambdas y máquinas de estado. No cuentan las constantes, que el compilador copia al IL sin referencia al tipo.
///
/// <para>
/// Esta clase conserva la propiedad original (Facturación -> solo Núcleo y bibliotecas permitidas, y lo nuevo de V-2 sin
/// deuda). La deuda anterior al registro (la pantalla «Facturación» del Operador CAE y el resumen por Cliente
/// empresarial, que se apoyan en el Cuadro de dirección, en Clientes y en otras áreas de Outbound) está congelada por
/// tipo de origen y tipo de destino en <c>Congelados/Modulos-Facturacion-a-fuera-del-Nucleo.txt</c>, que mantiene
/// <see cref="DependenciasEntreModulosTests"/>: solo puede encoger.
/// </para>
///
/// <para>
/// Diferencia con la versión anterior, por el registro: las áreas que ADR-013 § 3 da como propuesta y el registro marca
/// <c>provisional</c> en el Núcleo (Asignaciones, Documentos…) dejan de contar como deuda de Facturación. Si el registro
/// las mueve a Outbound, la lista congelada las recoge sola.
/// </para>
/// </summary>
public class FacturacionSoloDependeDelNucleoTests
{
    /// <summary>Raíces de namespace de BCL y de los paquetes que Facturación puede usar.</summary>
    private static readonly string[] BibliotecasPermitidas =
    [
        "System", "Microsoft", "MediatR", "FluentValidation", "ClosedXML", "DocumentFormat", "Npgsql", "Bunit",
    ];

    /// <summary>Lo nuevo de V-2, que nace sin deuda.</summary>
    private static readonly string[] TiposNuevosDeV2 =
    [
        "Application.Facturacion.LiquidacionesOperadorCae.",
        "Domain.Facturacion.",
        "Infrastructure.Persistence.Configurations.CobroLiquidacionOperadorCaeConfiguration",
        "Web.Features.Facturacion.LiquidacionOperadorCsv",
    ];

    [Fact]
    public void Facturacion_solo_referencia_el_Nucleo_y_las_bibliotecas_permitidas_salvo_la_deuda_congelada()
    {
        var medicion = TiposDeFacturacionMedidos();

        var externas = medicion.ExternasPorOrigen
            .Where(p => medicion.OrigenesDe(Modulo.Facturacion).Contains(p.Key))
            .SelectMany(p => p.Value.Where(ns => !BibliotecasPermitidas.Any(b => ns == b || ns.StartsWith(b + ".", StringComparison.Ordinal)))
                .Select(ns => $"{p.Key} -> {ns}"))
            .ToList();
        string.Join("\n", externas).Should().BeEmpty("Facturación solo usa BCL y los paquetes de BibliotecasPermitidas");

        var congelada = ListaCongelada.Leer(File.ReadAllText(ListaCongelada.RutaDeLista("Modulos-Facturacion-a-fuera-del-Nucleo")));
        var fuera = new[] { Modulo.Outbound, Modulo.Inbound }
            .SelectMany(destino => medicion.De(Modulo.Facturacion, destino)
                .SelectMany(p => p.Value.Select(d => new Ubicacion(p.Key, $"{destino}: {d}"))))
            .ToList();
        string.Join("\n", fuera.Where(u => !congelada.ContainsKey(u))).Should().BeEmpty(
            "Facturación depende solo del Núcleo (D-F2): para leer Outbound, un puerto del Núcleo como IFuenteLiquidacionOperadorCae");
    }

    [Fact]
    public void Lo_nuevo_de_la_liquidacion_del_Operador_CAE_no_tiene_deuda()
    {
        var medicion = TiposDeFacturacionMedidos();
        var origenes = medicion.OrigenesDe(Modulo.Facturacion).ToList();
        foreach (var prefijo in TiposNuevosDeV2)
            origenes.Should().Contain(t => t.StartsWith(prefijo, StringComparison.Ordinal),
                $"control del instrumento: {prefijo} tiene que estar entre los tipos que se miden");

        var fuera = new[] { Modulo.Outbound, Modulo.Inbound }
            .SelectMany(destino => medicion.De(Modulo.Facturacion, destino).Keys)
            .Where(t => TiposNuevosDeV2.Any(p => t.StartsWith(p, StringComparison.Ordinal)))
            .ToList();
        fuera.Should().BeEmpty("la marca de cobro y el CSV nacieron en el módulo y leen Outbound solo por el puerto del Núcleo");
    }

    private static AristasEntreModulos TiposDeFacturacionMedidos()
    {
        var medicion = MedicionDeSrc.Valor.Aristas;
        var tipos = medicion.OrigenesDe(Modulo.Facturacion).ToList();
        tipos.Should().NotBeEmpty("control del instrumento: si no encuentra tipos de Facturación, el vacío no es cumplimiento");
        tipos.Should().Contain(
        [
            "Application.Facturacion.LiquidacionesOperadorCae.MarcarCobroLiquidacionOperadorCaeCommandHandler",
            "Domain.Facturacion.CobroLiquidacionOperadorCae",
            "Infrastructure.Persistence.Configurations.CobroLiquidacionOperadorCaeConfiguration",
            "Web.Features.Facturacion.FacturacionEndpoints",
        ], "control del instrumento: tiene que mirar las cuatro capas de Facturación");
        return medicion;
    }
}
