using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Coherencia;

/// <summary>
/// Requisito del propietario antes de desplegar la tolerancia: la tolerancia NO cambia en silencio el conjunto de registros
/// que alimentan porcentajes, bloqueos y alertas, salvo donde la regla lo exige. Se toma la «foto» de todas las superficies
/// (<see cref="EscenarioDeFotoDeSuperficies"/>) sobre los MISMOS datos con tolerancia configurada y con tolerancia 0 en todo:
/// <list type="bullet">
/// <item>alertas, estado y % de Centro, Trabajador, Empresa, Cliente empresarial y Subcontrata, y la tarjeta de Inicio son
/// IDÉNTICOS: la tolerancia solo afecta al acceso, no al estado mostrado ni a ningún porcentaje (decisión del propietario);</item>
/// <item>Mi trabajo y los Trabajadores bloqueados por Centro difieren EXACTAMENTE en las filas que la regla exige, escritas a
/// mano abajo: un documento vencido que sigue dentro de la tolerancia del Centro deja de bloquear en ese Centro, y en ningún otro.</item>
/// </list>
/// La foto con tolerancia 0 en todo, contra el código anterior a este cambio, se midió una vez con este mismo escenario (cuerpo de
/// la PR): solo difiere en el bloqueo por Centro (una fila de Mi trabajo y un Trabajador bloqueado menos, el del certificado de
/// una Empresa en un Centro que no lo exige) y en que ya no existe la marca de alta nueva; alertas, estados y % son idénticos. Ese
/// resto (bloqueo por Centro y alta nueva bloqueada) lo fija <see cref="CoherenciaDelBloqueoDeAccesoEntreSuperficiesTests"/>.
/// </summary>
public class LaToleranciaNoCambiaEnSilencioLosRegistrosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private EscenarioDeFotoDeSuperficies _escenario = null!;

    public async Task InitializeAsync()
    {
        _escenario = new EscenarioDeFotoDeSuperficies(_cadenaConexion);
        await _escenario.SembrarAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private static readonly string[] SeccionesQueDependenDeLaTolerancia = ["Mi trabajo", "Trabajadores bloqueados por Centro"];

    [Fact]
    public async Task La_tolerancia_solo_cambia_el_bloqueo_y_exactamente_en_lo_que_la_regla_exige()
    {
        var fallos = new List<string>();

        var fotoUnoCon = await _escenario.TomarFotoAsync(_escenario.TenantUno);
        var fotoDosCon = await _escenario.TomarFotoAsync(_escenario.TenantDos);

        // El instrumento ve algo: las secciones que se comparan tienen contenido.
        foreach (var foto in new[] { fotoUnoCon, fotoDosCon })
        {
            foto["Mi trabajo"].Should().NotBeEmpty();
            foto["Alertas"].Should().NotBeEmpty();
            foto["Trabajador"].Should().NotBeEmpty();
            foto["Inicio"].Should().ContainSingle();
        }

        await FotoDeAcceso.QuitarTolerancias(_escenario);

        var fotoUnoSin = await _escenario.TomarFotoAsync(_escenario.TenantUno);
        var fotoDosSin = await _escenario.TomarFotoAsync(_escenario.TenantDos);

        // 1. Todo lo que NO es acceso es idéntico, sección a sección.
        foreach (var (nombre, con, sin) in new[] { ("Tenant uno", fotoUnoCon, fotoUnoSin), ("Tenant dos", fotoDosCon, fotoDosSin) })
        {
            con.Keys.Should().BeEquivalentTo(sin.Keys, "la foto tiene las mismas secciones");
            foreach (var seccion in con.Keys.Where(s => !SeccionesQueDependenDeLaTolerancia.Contains(s)))
            {
                if (!con[seccion].SequenceEqual(sin[seccion]))
                    fallos.Add($"{nombre} · «{seccion}» cambia con la tolerancia:\n  con:  {string.Join(" ; ", con[seccion])}\n  sin:  {string.Join(" ; ", sin[seccion])}");
            }
        }

        // 2. Mi trabajo y los bloqueados: la diferencia es exactamente la que dice la regla (oráculo a mano).
        //    Sin tolerancia (0 en todo) bloquean además, y solo en el Centro A (15 días del Cliente X para el PSS y el
        //    certificado), los vencidos hace 15 días o menos; el Centro B (personalizado a 0) y los Centros del Cliente Y
        //    (sin defecto) no cambian. El Tenant dos da 100 días a su Centro.
        string[] desaparecenEnUno =
        [
            "Centro A | p vencido ayer | PSS firmado | Trabajador | Vencido",
            "Centro A | p vencido hace 10 | PSS firmado | Trabajador | Vencido",
            "Centro A | p vencido hace 15 | PSS firmado | Trabajador | Vencido",
            "Centro A | q vencido ayer en A | PSS firmado | Trabajador | Vencido",
            "Centro A | r1 en A y B | Certificado de la Seguridad Social | Empresa | Vencido",
        ];
        string[] desaparecenEnDos = ["Centro A2 | t2 vencido hace 50 | PSS firmado | Trabajador | Vencido"];

        void Diferencia(string nombre, SortedDictionary<string, List<string>> con, SortedDictionary<string, List<string>> sin, string[] esperadas, string[] bloqueadosQueSeLibran)
        {
            var soloSin = sin["Mi trabajo"].Except(con["Mi trabajo"]).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var soloCon = con["Mi trabajo"].Except(sin["Mi trabajo"]).ToList();
            if (!soloSin.SequenceEqual(esperadas.OrderBy(x => x, StringComparer.Ordinal)))
                fallos.Add($"{nombre} · Mi trabajo: con tolerancia dejan de bloquear {string.Join(" ; ", soloSin)}; esperado {string.Join(" ; ", esperadas)}");
            if (soloCon.Count != 0)
                fallos.Add($"{nombre} · Mi trabajo: la tolerancia NO puede añadir filas y añade {string.Join(" ; ", soloCon)}");

            // Los Trabajadores bloqueados por Centro: se libran exactamente los que SOLO estaban bloqueados por documentos
            // dentro de tolerancia (oráculo a mano). r1 sigue bloqueado en el Centro B y q sigue bloqueado en A (le falta el certificado).
            var bloqueadosSin = sin["Trabajadores bloqueados por Centro"].ToHashSet();
            var bloqueadosCon = con["Trabajadores bloqueados por Centro"].ToHashSet();
            bloqueadosCon.IsSubsetOf(bloqueadosSin).Should().BeTrue("la tolerancia nunca bloquea a alguien nuevo");
            bloqueadosSin.Except(bloqueadosCon).Order(StringComparer.Ordinal).Should().Equal(bloqueadosQueSeLibran.Order(StringComparer.Ordinal),
                nombre + ": los Trabajadores que dejan de estar bloqueados con tolerancia");
        }

        Diferencia("Tenant uno", fotoUnoCon, fotoUnoSin, desaparecenEnUno,
            ["Centro A | p vencido ayer", "Centro A | p vencido hace 10", "Centro A | p vencido hace 15", "Centro A | r1 en A y B"]);
        Diferencia("Tenant dos", fotoDosCon, fotoDosSin, desaparecenEnDos, ["Centro A2 | t2 vencido hace 50"]);

        // 3. Lo que NO es tolerable sigue bloqueando con tolerancia: vencido hace 20 (más que los 15 de A), el certificado
        //    vencido en el Centro B (personalizado a 0), la Empresa sin certificado, las altas nuevas y el R2 por Centro.
        string[] siguenBloqueando =
        [
            "Centro A | p vencido hace 16 | PSS firmado | Trabajador | Vencido",
            "Centro A | p vencido hace 20 | PSS firmado | Trabajador | Vencido",
            "Centro A | p alta nueva sin documentos | PSS firmado | Trabajador | Ausente",
            "Centro B | r1 en A y B | Certificado de la Seguridad Social | Empresa | Vencido",
            "Centro A | q vencido ayer en A | Certificado de la Seguridad Social | Empresa | Ausente",
        ];
        foreach (var fila in siguenBloqueando)
            fotoUnoCon["Mi trabajo"].Should().Contain(fila, "la tolerancia no la alcanza: " + fila);
        fotoDosCon["Mi trabajo"].Should().Contain("Centro A2 | t2 alta nueva sin documentos | PSS firmado | Trabajador | Ausente");
        fotoUnoCon["Mi trabajo"].Should().NotContain(f => f.Contains("Centro D", StringComparison.Ordinal) && f.Contains("r2 solo en D", StringComparison.Ordinal),
            "el certificado de la Empresa R no se exige en el Centro D: el bloqueo es por Centro");

        fallos.Should().BeEmpty("la tolerancia solo cambia lo que la regla exige:\n" + string.Join("\n", fallos));
    }
}
