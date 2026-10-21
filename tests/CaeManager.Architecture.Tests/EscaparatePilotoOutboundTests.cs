using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Los tres Tenants escaparate del piloto Outbound (I, J y K), fijados sobre el catálogo y sin base de datos: el grande al
/// día no aparta nada de «en regla»; el de «por vencer» solo tiene Urgentes, todos a D+4; el de «pocos vencidos», exactamente
/// cinco vencidos, los de Trabajador con su reclamación. La medición sobre lo escrito en la base está en
/// <c>PilotoOutboundTests.Los_tres_Tenants_escaparate_tienen_el_estado_documental_que_venden</c> (integración, en CI).
/// </summary>
public class EscaparatePilotoOutboundTests
{
    private static readonly TenantPilotoOutbound I = CatalogoPilotoOutbound.I;
    private static readonly TenantPilotoOutbound J = CatalogoPilotoOutbound.J;
    private static readonly TenantPilotoOutbound K = CatalogoPilotoOutbound.K;

    private static readonly IReadOnlyList<TipoDocumento> Tipos = [.. TipoDocumentoSeedData.CrearCopiasParaTenant()];

    private static List<DesviacionDocumentoPilotoOutbound> Desviaciones(TenantPilotoOutbound tenant) =>
        CatalogoPilotoOutbound.Desviaciones(tenant, tenant.TrabajadoresSembrados, CatalogoPilotoOutbound.VencePorSuTipo);

    private static List<TipoDocumento> Exigidos(AmbitoAplicacion ambito) =>
        [.. Tipos.Where(t => t.AmbitoAplicacion == ambito && t.Requerido == RequisitoDocumental.Si).OrderBy(t => t.Orden).ThenBy(t => t.Nombre, StringComparer.Ordinal)];

    [Fact]
    public void Los_tres_estan_en_el_lote_con_Gestor_CAE_y_en_el_orden_de_siembra()
    {
        CatalogoPilotoOutbound.Escaparate.Should().Equal(I, J, K);
        CatalogoPilotoOutbound.Tenants.Should().Contain(CatalogoPilotoOutbound.Escaparate);

        // El Asistente IA, la Asignación de Operación y la cartera se siembran en el bucle de EnOrdenDeSiembra: que estén ahí
        // es lo que les da instrucción de tratamiento de IA vigente y el Encargo de administración.
        CatalogoPilotoOutbound.EnOrdenDeSiembra.Should().BeEquivalentTo(CatalogoPilotoOutbound.Tenants);
        CatalogoPilotoOutbound.NombresTenants.Should().Contain(CatalogoPilotoOutbound.Escaparate.Select(t => t.Nombre), "la retirada los reconoce");
        CatalogoPilotoOutbound.DiscrepanciasDelRepartoDeCarteras(CatalogoPilotoOutbound.Tenants).Should().BeEmpty();
        CatalogoPilotoOutbound.Escaparate.Should().OnlyContain(t => t.Cartera != CarteraPilotoOutbound.Compartida, "el compartido sigue siendo solo C");
    }

    [Fact]
    public void Los_tres_usan_todas_las_capacidades_que_siembra_el_piloto()
    {
        foreach (var t in CatalogoPilotoOutbound.Escaparate)
        {
            t.Subcontratas.Should().NotBeEmpty($"{t.Clave} tiene Subcontratas").And.OnlyContain(s => s.Trabajadores > 0 && !s.Supervisada);
            t.Vehiculos.Should().Contain(v => v.Tipo == "Maquinaria de obra" || v.Tipo == "Camión", $"{t.Clave} tiene maquinaria o camión");
            t.Obras.Should().NotBeEmpty($"{t.Clave} tiene Proyectos");
            t.Visitas.Should().Contain(v => v.Estado == EstadoVisitaPilotoOutbound.Futura).And.Contain(v => v.Estado == EstadoVisitaPilotoOutbound.Realizada);
            t.CentroConPlataforma.Should().NotBeNull($"{t.Clave} acredita en plataforma");
            t.AcreditaDocumentosDeEmpresa.Should().BeTrue($"{t.Clave} acredita también los documentos de Empresa de sus Subcontratas");
            t.ConSenalesDeIa.Should().BeTrue($"{t.Clave} tiene filas de la IA en Mi trabajo");
            t.BajasDelUltimoAno.Should().BePositive($"{t.Clave} tiene bajas para la rotación del Cuadro de dirección");
            t.Receta.AcreditacionesSubidas.Should().Be(0);
            t.Receta.AcreditacionesRechazadas.Should().Be(0);
            t.Receta.Ausentes.Should().Be(0, $"a {t.Clave} no le falta ningún documento");
            t.Receta.SinConfirmar.Should().Be(0);
            t.TiposDeEmpresaNoExigidos.Should().BeEquivalentTo(CatalogoPilotoOutbound.CertificadosMensualesDeEmpresa,
                "un certificado mensual coherente solo puede estar «Próximo»: se excluye en sus Centros, como en A");
        }

        I.TrabajadoresSembrados.Should().BeInRange(60, 90, "el grande tiene entre sesenta y noventa Trabajadores");
        I.Centros.Should().HaveCountGreaterThanOrEqualTo(5);
        I.ClientesEmpresariales.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void El_grande_al_dia_no_tiene_nada_vencido_ni_por_vencer_ni_que_falte()
    {
        Desviaciones(I).Should().BeEmpty();
        var inicio = CatalogoPilotoOutbound.DocumentosEsperadosEnInicio(I);
        (inicio.Vencidos.Maximo, inicio.Urgentes.Maximo, inicio.Proximos.Maximo, inicio.SinConfirmar).Should().Be((0, 0, 0, 0));

        I.Vehiculos.Should().OnlyContain(v => v.Situacion == null);
        I.Subcontratas.Should().OnlyContain(s => s.Situacion == null);
        I.SituacionDocumentoDeEmpresa.Should().BeNull();
        (I.Tolerancia, I.CentroConRequisitoBloqueante, I.UrgentesVencenALosDias).Should().Be((null, null, null));
        I.Extranjeros.Should().OnlyContain(e => e.DiasHastaLaCaducidad > 30 + OpcionesPilotoOutbound.MargenMaximoDias);
        // El permiso de trabajo es de fecha manual: su emisión se calcula doce meses antes del vencimiento y no puede
        // ser futura ningún día de siembra (D − MargenMaximoDias).
        CatalogoPilotoOutbound.Tenants.SelectMany(t => t.Extranjeros)
            .Should().OnlyContain(e => e.DiasHastaLaCaducidad < 365 - OpcionesPilotoOutbound.MargenMaximoDias);
        (I.GestionesPendientes, I.ReclamacionesSinRespuesta, I.ReclamacionesRespondidas).Should().Be((0, 0, 0));
    }

    [Fact]
    public void El_de_por_vencer_solo_tiene_Urgentes_en_los_cuatro_ambitos()
    {
        J.UrgentesVencenALosDias.Should().Be(4);
        Desviaciones(J).Should().HaveCount(6).And.OnlyContain(d => d.Situacion == SituacionDocumentoPilotoOutbound.Urgente);
        var inicio = CatalogoPilotoOutbound.DocumentosEsperadosEnInicio(J);
        (inicio.Vencidos.Maximo, inicio.Urgentes.Minimo, inicio.Urgentes.Maximo, inicio.Proximos.Maximo).Should().Be((0, 6, 6, 0));

        J.Vehiculos.Count(v => v.Situacion == SituacionDocumentoPilotoOutbound.Urgente).Should().Be(1);
        J.Vehiculos.Should().OnlyContain(v => v.Situacion == null || v.Situacion == SituacionDocumentoPilotoOutbound.Urgente);
        J.Subcontratas.Select(s => s.Situacion).Should().BeEquivalentTo(new SituacionDocumentoPilotoOutbound?[] { null, SituacionDocumentoPilotoOutbound.Urgente });
        J.SituacionDocumentoDeEmpresa.Should().Be(SituacionDocumentoPilotoOutbound.Urgente);
        J.Extranjeros.Should().BeEmpty();
    }

    /// <summary>
    /// D+4 es exacto en todos los tipos que el de «por vencer» pone Urgentes, cualquier día de la demostración de los
    /// próximos años, salvo cuando D+4 es un 29 de febrero: <see cref="FechasPilotoOutbound.ConVencimiento"/> retrocede un
    /// vencimiento que ninguna emisión alcanza, y ese día vencería el 28.
    /// </summary>
    [Fact]
    public void Los_Urgentes_del_de_por_vencer_vencen_exactamente_a_D_mas_4_cualquier_dia()
    {
        var tiposDeTrabajador = Desviaciones(J).Select(d => d.Tipo).Distinct().Select(n => Tipos.Single(t => t.Nombre == n));
        var tipoDeVehiculo = Exigidos(AmbitoAplicacion.Vehiculo)[0];
        var tipoDeEmpresa = Exigidos(AmbitoAplicacion.Empresa)
            .Where(t => !J.TiposDeEmpresaNoExigidos.Contains(t.Nombre))
            .First(t => !CatalogoPilotoOutbound.TiposQueNoCaducan.Contains(t.Nombre) && !(t.AplicaVencimientoAutomatico && t.VigenciaMeses is <= 1));
        var tipoDeSubcontrata = Tipos.Single(t => t.Nombre == CatalogoPilotoOutbound.TiposDeDocumentoDeSubcontrata[0]);
        var tipos = tiposDeTrabajador.Append(tipoDeVehiculo).Append(tipoDeEmpresa).Append(tipoDeSubcontrata).ToList();

        var fallos = new List<string>();
        for (var d = new DateOnly(2026, 10, 1); d <= new DateOnly(2030, 12, 31); d = d.AddDays(1))
        {
            // MEDIDO: la única excepción es un D+4 que cae en 29 de febrero, al que ninguna emisión anual llega: vence el 28.
            if (d.AddDays(4) is { Month: 2, Day: 29 }) continue;

            var fechas = new FechasPilotoOutbound(d);
            foreach (var tipo in tipos)
            {
                var (_, vence) = fechas.ConVencimiento(tipo, d.AddDays(J.UrgentesVencenALosDias!.Value));
                if (vence != d.AddDays(4)) fallos.Add($"{d:yyyy-MM-dd} «{tipo.Nombre}»: vence {vence:yyyy-MM-dd}");
            }
        }

        fallos.Should().BeEmpty();
    }

    [Fact]
    public void El_de_pocos_vencidos_tiene_exactamente_cinco_y_reclama_los_de_Trabajador()
    {
        Desviaciones(K).Should().HaveCount(3).And.OnlyContain(d => d.Situacion == SituacionDocumentoPilotoOutbound.Vencido);
        var vencidos = Desviaciones(K).Count
            + K.Vehiculos.Count(v => v.Situacion == SituacionDocumentoPilotoOutbound.Vencido)
            + K.Subcontratas.Count(s => s.Situacion == SituacionDocumentoPilotoOutbound.Vencido)
            + (K.SituacionDocumentoDeEmpresa == SituacionDocumentoPilotoOutbound.Vencido ? 1 : 0);
        vencidos.Should().Be(5);

        K.Vehiculos.Should().OnlyContain(v => v.Situacion == null || v.Situacion == SituacionDocumentoPilotoOutbound.Vencido);
        K.Subcontratas.Should().OnlyContain(s => s.Situacion == null || s.Situacion == SituacionDocumentoPilotoOutbound.Vencido);
        (K.ReclamacionesSinRespuesta + K.ReclamacionesRespondidas).Should().Be(3, "cada vencido de Trabajador tiene su reclamación");
        var inicio = CatalogoPilotoOutbound.DocumentosEsperadosEnInicio(K);
        (inicio.Vencidos.Minimo, inicio.Vencidos.Maximo, inicio.Urgentes.Maximo, inicio.Proximos.Maximo).Should().Be((3, 3, 0, 0));
    }

    [Fact]
    public void Las_bajas_del_ultimo_ano_tienen_un_documento_valido_que_no_repite_el_de_ningun_Trabajador()
    {
        var delLote = CatalogoPilotoOutbound.Tenants
            .SelectMany(t => Enumerable.Range(0, t.TrabajadoresSembrados).Select(i => IdentidadesPilotoOutbound.Dni(t, i))).ToHashSet();
        var bajas = CatalogoPilotoOutbound.Tenants
            .SelectMany(t => Enumerable.Range(0, t.BajasDelUltimoAno).Select(k => IdentidadesPilotoOutbound.DniDeBaja(t, k))).ToList();

        bajas.Should().NotBeEmpty().And.OnlyHaveUniqueItems();
        bajas.Should().OnlyContain(dni => ValidadorIdentificacion.EsIdentificacionFiscalValida(dni) && !delLote.Contains(dni));
    }
}
