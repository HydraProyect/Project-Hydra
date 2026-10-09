using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Las fechas que la siembra del piloto Outbound da a un documento
/// (<see cref="FechasPilotoOutbound"/>) son las que el producto le habría dado: en un
/// Tipo con vencimiento automático, el vencimiento es la emisión más los meses del
/// Tipo (<see cref="CalculadoraEstadoDocumento.ResolverVigencia"/>), y ninguna emisión
/// es futura el día en que se siembra.
///
/// <para>
/// Es aritmética de calendario —meses de 28, 29, 30 y 31 días— y depende del día
/// de la demostración, que cambia en cada siembra. Los tests de integración la ven
/// con UNA fecha; este la recorre con todas las de cinco años, contra el catálogo
/// semilla entero y sin base de datos. La autoverificación de la siembra
/// (<see cref="PilotoOutboundAutoverificacion"/>) mide después lo mismo sobre los
/// documentos ya escritos.
/// </para>
/// </summary>
public class FechasPilotoOutboundCoherentesConElTipoTests
{
    // Los umbrales por defecto de un Tenant, con los que se pintan los estados del piloto.
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;

    private static readonly IReadOnlyList<TipoDocumento> Catalogo =
        [.. TipoDocumentoSeedData.Datos.Select(d => new TipoDocumento(d.Nombre, d.VigenciaMeses, d.AplicaVencimiento, d.Orden, d.Ambito))];

    // Las semillas con la forma que les da el contador de la siembra: un múltiplo de 37 más un desplazamiento por Tenant.
    private static readonly int[] Semillas = [.. Enumerable.Range(0, 48).Select(i => i * 37 + i % 6 * 11)];

    /// <summary>Cada día de 2026 a 2030 como día de la demostración: hay un 29 de febrero dentro y otro al alcance de los vencimientos.</summary>
    private static IEnumerable<FechasPilotoOutbound> Demostraciones()
    {
        for (var d = new DateOnly(2026, 1, 1); d <= new DateOnly(2030, 12, 31); d = d.AddDays(1))
            yield return new FechasPilotoOutbound(d);
    }

    private static EstadoDocumento Estado(DateOnly vence, DateOnly hoy) =>
        CalculadoraEstadoDocumento.Calcular(VigenciaDocumento.VenceEl(vence), hoy, UmbralAmbarDias, UmbralRojoDias);

    private static DateOnly PrimerDiaDeSiembra(FechasPilotoOutbound fechas) =>
        fechas.FechaDemostracion.AddDays(-OpcionesPilotoOutbound.MargenMaximoDias);

    [Fact]
    public void Un_documento_en_regla_tiene_las_fechas_que_el_producto_le_daria_y_el_estado_que_su_tipo_permite()
    {
        var fallos = new List<string>();
        var casos = 0;
        var mensuales = new SortedDictionary<int, int>();

        foreach (var fechas in Demostraciones())
        {
            var d = fechas.FechaDemostracion;
            var primerDia = PrimerDiaDeSiembra(fechas);

            foreach (var tipo in Catalogo)
            {
                for (var s = 0; s < Semillas.Length; s++)
                {
                    // Las dos formas en que la siembra pide las fechas: con una semilla para cada una, o con una sola.
                    var (emision, vence) = s % 2 == 0
                        ? fechas.EnRegla(tipo, Semillas[s], Semillas[^(s + 1)])
                        : fechas.EnRegla(tipo, Semillas[s]);
                    casos++;

                    void Falla(string motivo) =>
                        fallos.Add($"D = {d:yyyy-MM-dd} · «{tipo.Nombre}» · emisión {emision:yyyy-MM-dd}, vence {vence:yyyy-MM-dd}: {motivo}");

                    if (emision > primerDia)
                        Falla("la emisión es posterior al primer día en que se puede sembrar");

                    if (tipo.FijaVigenciaDesdeLaEmision && vence != CalculadoraEstadoDocumento.CalcularFechaVencimiento(emision, tipo.VigenciaMeses))
                        Falla("el vencimiento no es la emisión más los meses del Tipo");

                    if (CatalogoPilotoOutbound.TiposQueNoCaducan.Contains(tipo.Nombre) != (vence is null))
                        Falla("«no caduca» es de los cinco Tipos de la lista, y solo de ellos");

                    if (vence is not { } fecha) continue;

                    var esperado = tipo is { AplicaVencimientoAutomatico: true, VigenciaMeses: 1 } ? EstadoDocumento.Proximo : EstadoDocumento.Vigente;
                    if (Estado(fecha, primerDia) != esperado || Estado(fecha, d) != esperado)
                        Falla($"no está {esperado} el primer día de siembra y el de la demostración ({Estado(fecha, primerDia)}, {Estado(fecha, d)})");

                    if (esperado == EstadoDocumento.Proximo)
                        mensuales[fecha.DayNumber - d.DayNumber] = mensuales.GetValueOrDefault(fecha.DayNumber - d.DayNumber) + 1;
                }
            }
        }

        // Control positivo: el recorrido miró algo, y los mensuales cayeron donde el diseño dice.
        casos.Should().BeGreaterThan(1_000_000);
        mensuales.Keys.Should().OnlyContain(dias => dias >= 16 && dias <= 21, "un mensual vence entre D+16 y D+21: «Próximo» de D−9 a D");
        mensuales.Should().ContainKeys(20, 21);
        fallos.Take(10).Should().BeEmpty($"hay {fallos.Count} casos en {casos}");
    }

    [Fact]
    public void Un_vencimiento_del_diseno_conserva_su_estado_y_solo_retrocede_el_dia_que_ninguna_emision_alcanza()
    {
        var fallos = new List<string>();
        var casos = 0;
        var movidos = 0;

        // Los Tipos a los que el diseño pone un vencimiento a propósito: los que vencen solos a doce meses o más, y los de fecha manual.
        var tipos = Catalogo.Where(t => t is { AplicaVencimientoAutomatico: false } or { VigenciaMeses: >= 12 }).ToList();
        tipos.Should().Contain(t => t.VigenciaMeses == 12).And.Contain(t => t.VigenciaMeses == 36).And.Contain(t => !t.AplicaVencimientoAutomatico);

        foreach (var fechas in Demostraciones())
        {
            var d = fechas.FechaDemostracion;
            var primerDia = PrimerDiaDeSiembra(fechas);

            foreach (var semilla in Semillas.Take(12))
            {
                (DateOnly Vence, EstadoDocumento Estado)[] delDiseno =
                [
                    (fechas.Vigente(semilla), EstadoDocumento.Vigente), (fechas.Proximo(semilla), EstadoDocumento.Proximo),
                    (fechas.Urgente(semilla), EstadoDocumento.Urgente), (fechas.Vencido(semilla), EstadoDocumento.Vencido)
                ];

                foreach (var tipo in tipos)
                {
                    foreach (var (vencimientoDeDiseno, estado) in delDiseno)
                    {
                        var (emision, vence) = fechas.ConVencimiento(tipo, vencimientoDeDiseno);
                        casos++;

                        void Falla(string motivo) =>
                            fallos.Add($"D = {d:yyyy-MM-dd} · «{tipo.Nombre}» · diseño {vencimientoDeDiseno:yyyy-MM-dd} · emisión {emision:yyyy-MM-dd}, vence {vence:yyyy-MM-dd}: {motivo}");

                        if (emision > primerDia)
                            Falla("la emisión es posterior al primer día en que se puede sembrar");

                        if (tipo.FijaVigenciaDesdeLaEmision && vence != CalculadoraEstadoDocumento.CalcularFechaVencimiento(emision, tipo.VigenciaMeses))
                            Falla("el vencimiento no es la emisión más los meses del Tipo");

                        if (Estado(vence, primerDia) != estado || Estado(vence, d) != estado)
                            Falla($"no está {estado} el primer día de siembra y el de la demostración");

                        if (vence == vencimientoDeDiseno) continue;

                        movidos++;
                        if (vencimientoDeDiseno is not { Month: 2, Day: 29 } || vence != vencimientoDeDiseno.AddDays(-1) || !tipo.AplicaVencimientoAutomatico)
                            Falla("el vencimiento del diseño solo puede moverse un día, y solo desde un 29 de febrero en un Tipo que vence solo");
                    }
                }
            }
        }

        casos.Should().BeGreaterThan(1_000_000);
        // Control positivo: el recorrido pasó por el caso que obliga a mover el vencimiento.
        movidos.Should().BeGreaterThan(0, "entre 2026 y 2030 hay vencimientos de diseño en 29 de febrero");
        fallos.Take(10).Should().BeEmpty($"hay {fallos.Count} casos en {casos}");
    }

    [Fact]
    public void Un_documento_del_que_el_diseno_fija_la_emision_vence_cuando_el_producto_lo_calcularia()
    {
        var aptitud = Catalogo.Single(t => t.Nombre == DisenoT5PilotoOutbound.TipoConCondicionesPorCentro);

        foreach (var fechas in Demostraciones())
        {
            var emision = fechas.FechaDemostracion
                .AddDays(-DisenoT5PilotoOutbound.DiasDesdeElVencimientoPorPeriodicidad).AddMonths(-DisenoT5PilotoOutbound.MesesDePeriodicidadEspecial);

            var (emitido, vence) = fechas.DesdeEmision(aptitud, emision);

            emitido.Should().Be(emision);
            vence.Should().Be(CalculadoraEstadoDocumento.CalcularFechaVencimiento(emision, aptitud.VigenciaMeses));
        }
    }

    [Fact]
    public void Los_tipos_que_la_siembra_deja_sin_caducidad_son_del_catalogo_y_ninguno_vence_solo()
    {
        var porNombre = TipoDocumentoSeedData.Datos.ToDictionary(d => d.Nombre);

        CatalogoPilotoOutbound.TiposQueNoCaducan.Should().HaveCount(5).And.OnlyHaveUniqueItems();
        foreach (var nombre in CatalogoPilotoOutbound.TiposQueNoCaducan.Append(CatalogoPilotoOutbound.DocumentoIdentidad))
        {
            porNombre.Should().ContainKey(nombre, "si el catálogo semilla renombra el Tipo, la siembra del piloto dejaría de reconocerlo y le pondría otra vigencia");
            porNombre[nombre].AplicaVencimiento.Should().BeFalse($"«{nombre}» tendría que seguir sin vencimiento automático");
        }

        // El documento de identidad se reparte en varios años, no dentro de los diez meses siguientes a la demostración.
        var fechas = new FechasPilotoOutbound(new DateOnly(2026, 10, 14));
        var identidad = Catalogo.Single(t => t.Nombre == CatalogoPilotoOutbound.DocumentoIdentidad);
        var anos = Enumerable.Range(0, 200).Select(i => fechas.EnRegla(identidad, i * 37).Vence!.Value.Year).Distinct().ToList();
        anos.Should().HaveCountGreaterThanOrEqualTo(5);
    }
}
