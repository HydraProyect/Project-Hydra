using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Coherencia;

/// <summary>
/// Requisito del propietario antes de desplegar el vencimiento por Centro (2026-10-04): la periodicidad propia de un Centro y las
/// presentaciones del Documento en ese Centro NO cambian en silencio los registros que alimentan porcentajes, bloqueos y alertas,
/// salvo donde la regla lo exige: <c>vence en el Centro = min(última presentación en el Centro + meses, vigencia propia)</c>, con la
/// emisión como ancla si no hay presentación. Se toma la «foto» de todas las superficies (<see cref="EscenarioDeFotoDeSuperficies"/>,
/// la misma de la tolerancia) antes y después sobre los MISMOS datos:
/// <list type="bullet">
/// <item>sin periodicidad en ningún Centro, sembrar presentaciones no cambia NINGUNA sección en ninguno de los dos Tenants;</item>
/// <item>con periodicidad en un solo Centro y Tipo, solo cambian Mi trabajo y los bloqueados por Centro, solo en las filas de ESE
/// Centro, y exactamente en las que dicta la regla (oráculo escrito a mano abajo);</item>
/// <item>presentar el documento en ese Centro devuelve la fila a su estado de antes salvo en lo que vence por su propia fecha, y
/// presentarlo en otro Centro no cambia nada.</item>
/// </list>
/// Escenario: los documentos se emitieron hace 400 días, así que con 12 meses de periodicidad y sin presentar, todo documento
/// que caduca vence en ese Centro unos 35 días atrás, fuera de los 15 días de tolerancia del Cliente empresarial X.
/// </summary>
public class VencimientoPorCentroNoCambiaEnSilencioLosRegistrosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private EscenarioDeFotoDeSuperficies _escenario = null!;

    public async Task InitializeAsync()
    {
        _escenario = new EscenarioDeFotoDeSuperficies(_cadenaConexion);
        await _escenario.SembrarAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private static readonly string[] SeccionesDeAcceso = ["Mi trabajo", "Trabajadores bloqueados por Centro"];

    private async Task PeriodicidadEnCentroAAsync(int? meses)
    {
        await using var c = _escenario.CrearContexto(_escenario.TenantUno);
        var centroA = await c.Centros.SingleAsync(x => x.Nombre == "Centro A");
        var pss = await c.TiposDocumento.SingleAsync(t => t.Nombre == "PSS firmado");
        var fila = await c.TiposDocumentoCentros.SingleAsync(f => f.CentroId == centroA.Id && f.TipoDocumentoId == pss.Id);
        fila.Actualizar(fila.Incluido, meses, fila.BloqueaAcceso, fila.ArchivoUrl, fila.NombreArchivoOriginal, fila.ToleranciaDias);
        await c.SaveChangesAsync();
    }

    /// <summary>Presenta HOY el PSS del Trabajador de apellidos dados en el Centro dado (origen «Volver a presentar»).</summary>
    private async Task PresentarAsync(string apellidosTrabajador, string centro)
    {
        await using var c = _escenario.CrearContexto(_escenario.TenantUno);
        var trabajador = await c.Trabajadores.SingleAsync(t => t.Apellidos == apellidosTrabajador);
        var centroId = (await c.Centros.SingleAsync(x => x.Nombre == centro)).Id;
        var documentoId = (await c.Documentos.SingleAsync(d => d.TrabajadorId == trabajador.Id)).Id;
        c.PresentacionesDocumentoEnCentro.Add(new PresentacionDocumentoEnCentro(
            documentoId, centroId, DiaDeNegocio.Hoy(), OrigenPresentacionDocumentoEnCentro.VolverAPresentar, DateTime.UtcNow));
        await c.SaveChangesAsync();
    }

    private static void ExigirIgualesSalvo(
        string nombre, SortedDictionary<string, List<string>> antes, SortedDictionary<string, List<string>> despues, string[] seccionesQueCambian, List<string> fallos)
    {
        antes.Keys.Should().BeEquivalentTo(despues.Keys, "la foto tiene las mismas secciones");
        foreach (var seccion in antes.Keys.Where(s => !seccionesQueCambian.Contains(s)))
        {
            if (!antes[seccion].SequenceEqual(despues[seccion]))
                fallos.Add($"{nombre} · «{seccion}» cambia:\n  antes:   {string.Join(" ; ", antes[seccion])}\n  despues: {string.Join(" ; ", despues[seccion])}");
        }
    }

    [Fact]
    public async Task Sin_periodicidad_en_ningun_Centro_las_presentaciones_no_cambian_ninguna_seccion()
    {
        var unoAntes = await _escenario.TomarFotoAsync(_escenario.TenantUno);
        var dosAntes = await _escenario.TomarFotoAsync(_escenario.TenantDos);
        foreach (var foto in new[] { unoAntes, dosAntes })
        {
            foto["Mi trabajo"].Should().NotBeEmpty("el instrumento ve algo");
            foto["Alertas"].Should().NotBeEmpty();
        }

        // Presentaciones en varios Centros y Trabajadores, de vigentes y de vencidos: ninguna fila tiene periodicidad.
        foreach (var (trabajador, centro) in new[] { ("p vigente en A y B", "Centro A"), ("p vigente en A y B", "Centro B"), ("p vencido ayer", "Centro A"), ("p vencido hace 20", "Centro A"), ("p proximo", "Centro A") })
            await PresentarAsync(trabajador, centro);

        var unoDespues = await _escenario.TomarFotoAsync(_escenario.TenantUno);
        var dosDespues = await _escenario.TomarFotoAsync(_escenario.TenantDos);

        var fallos = new List<string>();
        ExigirIgualesSalvo("Tenant uno", unoAntes, unoDespues, [], fallos);
        ExigirIgualesSalvo("Tenant dos", dosAntes, dosDespues, [], fallos);
        fallos.Should().BeEmpty("sin periodicidad en el Centro, una presentacion no cambia nada:\n" + string.Join("\n", fallos));
    }

    [Fact]
    public async Task Con_periodicidad_solo_cambian_las_filas_de_ese_Centro_y_Tipo_exactamente_las_que_dicta_la_regla()
    {
        var unoAntes = await _escenario.TomarFotoAsync(_escenario.TenantUno);
        var dosAntes = await _escenario.TomarFotoAsync(_escenario.TenantDos);

        await PeriodicidadEnCentroAAsync(12);

        var unoDespues = await _escenario.TomarFotoAsync(_escenario.TenantUno);
        var dosDespues = await _escenario.TomarFotoAsync(_escenario.TenantDos);

        var fallos = new List<string>();
        ExigirIgualesSalvo("Tenant uno", unoAntes, unoDespues, SeccionesDeAcceso, fallos);
        ExigirIgualesSalvo("Tenant dos", dosAntes, dosDespues, [], fallos);

        // Mi trabajo: nada de los Centros B, C y D cambia; en el Centro A, el PSS de todo Trabajador cuyo documento caduca pasa a
        // estar vencido en el Centro (emitido hace 400 días: 12 meses desde la emisión fue hace ~35 días, fuera de los 15 de
        // tolerancia), aunque su vigencia propia dijera otra cosa; «No caduca» y las altas nuevas sin documento no cambian.
        foreach (var seccion in SeccionesDeAcceso)
        {
            var antesFuera = unoAntes[seccion].Where(f => !f.StartsWith("Centro A |", StringComparison.Ordinal)).ToList();
            var despuesFuera = unoDespues[seccion].Where(f => !f.StartsWith("Centro A |", StringComparison.Ordinal)).ToList();
            antesFuera.SequenceEqual(despuesFuera).Should().BeTrue($"«{seccion}»: los Centros B, C y D no cambian");
        }

        string[] filasNuevasEnMiTrabajo =
        [
            "Centro A | p vencido ayer | PSS firmado | Trabajador | Vencido",
            "Centro A | p vencido hace 10 | PSS firmado | Trabajador | Vencido",
            "Centro A | p vencido hace 15 | PSS firmado | Trabajador | Vencido",
            "Centro A | p vence hoy | PSS firmado | Trabajador | Vencido",
            "Centro A | p proximo | PSS firmado | Trabajador | Vencido",
            "Centro A | p vigente en A y B | PSS firmado | Trabajador | Vencido",
            "Centro A | p sin confirmar | PSS firmado | Trabajador | Vencido",
            "Centro A | q vencido ayer en A | PSS firmado | Trabajador | Vencido",
            "Centro A | r1 en A y B | PSS firmado | Trabajador | Vencido",
        ];
        unoDespues["Mi trabajo"].Except(unoAntes["Mi trabajo"]).Order(StringComparer.Ordinal).Should().Equal(
            filasNuevasEnMiTrabajo.Order(StringComparer.Ordinal), "las filas de Mi trabajo que aparecen por la periodicidad del Centro A");
        unoAntes["Mi trabajo"].Except(unoDespues["Mi trabajo"]).Should().BeEmpty("la periodicidad no libra a nadie");

        string[] bloqueadosNuevos =
        [
            "Centro A | p vencido ayer", "Centro A | p vencido hace 10", "Centro A | p vencido hace 15", "Centro A | p vence hoy",
            "Centro A | p proximo", "Centro A | p vigente en A y B", "Centro A | p sin confirmar", "Centro A | r1 en A y B",
        ];
        unoDespues["Trabajadores bloqueados por Centro"].Except(unoAntes["Trabajadores bloqueados por Centro"]).Order(StringComparer.Ordinal)
            .Should().Equal(bloqueadosNuevos.Order(StringComparer.Ordinal), "los Trabajadores que pasan a estar bloqueados en el Centro A");
        unoAntes["Trabajadores bloqueados por Centro"].Except(unoDespues["Trabajadores bloqueados por Centro"]).Should().BeEmpty();

        // Lo que no cambia aunque el Centro A tenga periodicidad: «No caduca» y la alta nueva.
        unoDespues["Mi trabajo"].Should().NotContain(f => f.Contains("p no caduca", StringComparison.Ordinal), "No caduca no vence nunca (hueco declarado)");

        fallos.Should().BeEmpty(string.Join("\n", fallos));
    }

    [Fact]
    public async Task Presentar_el_documento_en_el_Centro_reinicia_su_plazo_alli_y_solo_alli_y_no_alarga_la_vigencia_propia()
    {
        await PeriodicidadEnCentroAAsync(12);
        var sinPresentar = await _escenario.TomarFotoAsync(_escenario.TenantUno);

        // Presentado HOY en el Centro A: vence en el Centro dentro de 12 meses, acotado por su vigencia propia (+200 dias).
        await PresentarAsync("p vigente en A y B", "Centro A");
        // Presentado en el Centro B (sin periodicidad): no cambia nada en el A.
        await PresentarAsync("p proximo", "Centro B");
        // Presentado hoy en A pero vencido por su propia fecha desde hace 20 dias: presentar no lo devuelve a la vida.
        await PresentarAsync("p vencido hace 20", "Centro A");

        var presentado = await _escenario.TomarFotoAsync(_escenario.TenantUno);

        presentado["Mi trabajo"].Should().NotContain("Centro A | p vigente en A y B | PSS firmado | Trabajador | Vencido");
        presentado["Trabajadores bloqueados por Centro"].Should().NotContain("Centro A | p vigente en A y B");
        presentado["Mi trabajo"].Should().Contain("Centro A | p proximo | PSS firmado | Trabajador | Vencido", "presentar en el Centro B no lo arregla en el A");
        presentado["Mi trabajo"].Should().Contain("Centro A | p vencido hace 20 | PSS firmado | Trabajador | Vencido", "un documento vencido por su fecha se renueva");
        sinPresentar["Mi trabajo"].Should().Contain("Centro A | p vigente en A y B | PSS firmado | Trabajador | Vencido");

        // El resto de filas de Mi trabajo no cambia, y las otras secciones tampoco.
        presentado["Mi trabajo"].Except(sinPresentar["Mi trabajo"]).Should().BeEmpty();
        sinPresentar["Mi trabajo"].Except(presentado["Mi trabajo"]).Should().Equal(["Centro A | p vigente en A y B | PSS firmado | Trabajador | Vencido"]);
        var fallos = new List<string>();
        ExigirIgualesSalvo("Tenant uno", sinPresentar, presentado, SeccionesDeAcceso, fallos);
        fallos.Should().BeEmpty(string.Join("\n", fallos));
    }
}
