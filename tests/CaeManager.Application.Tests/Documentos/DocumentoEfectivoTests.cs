using CaeManager.Application.Documentos;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// El orden puro del documento efectivo (diseño del documento efectivo, 2026-10-03, § 2.4): válido hoy → nominativo
/// (D3, inerte hasta la PR 6) → emisión más reciente → vigencia confirmada → <c>CreadoEnUtc</c> → <c>Id</c>. Cada paso se
/// aísla variando UNA sola clave y manteniendo iguales las anteriores, para que ningún paso preste evidencia a otro.
/// Reúne lo que probaban las dos preferencias que absorbe (<c>PreferenciaDocumentoPorTipo</c> y
/// <c>PreferenciaCopiaDelPaquete</c>); la que desaparece es «vence más tarde».
/// </summary>
public class DocumentoEfectivoTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 3);
    private static readonly DateTime Alta = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private sealed record Copia(
        string Nombre, DateOnly Emision, EstadoVigenciaDocumento Estado, DateOnly? Vencimiento, DateTime Creado, Guid Id, bool Nominativo);

    private static Copia Nueva(
        string nombre, int emisionHaceDias, EstadoVigenciaDocumento estado = EstadoVigenciaDocumento.VenceEnFecha,
        int? venceEnDias = 100, int altaMinutos = 0, Guid? id = null, bool nominativo = false) =>
        new(nombre, Hoy.AddDays(-emisionHaceDias), estado,
            estado == EstadoVigenciaDocumento.VenceEnFecha ? Hoy.AddDays(venceEnDias!.Value) : null,
            Alta.AddMinutes(altaMinutos), id ?? Guid.NewGuid(), nominativo);

    private static List<string> Orden(params Copia[] copias) =>
        DocumentoEfectivo.Ordenar(copias, c => c.Estado, c => c.Vencimiento, c => c.Emision, c => c.Creado, c => c.Id, Hoy)
            .Select(c => c.Nombre).ToList();

    private static List<string> OrdenConNominativo(params Copia[] copias) =>
        DocumentoEfectivo.Ordenar(copias, c => c.Estado, c => c.Vencimiento, c => c.Emision, c => c.Creado, c => c.Id, Hoy,
                esNominativo: c => c.Nominativo)
            .Select(c => c.Nombre).ToList();

    // ---------- 1. Válido hoy ----------

    [Fact]
    public void El_valido_hoy_gana_al_vencido_aunque_el_vencido_tenga_la_emision_mas_reciente()
    {
        Orden(Nueva("vencido-reciente", 2, venceEnDias: -1), Nueva("valido-antiguo", 300, venceEnDias: 40))
            .Should().Equal("valido-antiguo", "vencido-reciente");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    public void Vence_hoy_todavia_vale_y_ayer_no(int venceEnDias, bool valido)
    {
        DocumentoEfectivo.ValidoHoy(EstadoVigenciaDocumento.VenceEnFecha, Hoy.AddDays(venceEnDias), Hoy).Should().Be(valido);
    }

    [Theory]
    [InlineData(EstadoVigenciaDocumento.NoCaduca)]
    [InlineData(EstadoVigenciaDocumento.SinConfirmar)]
    public void Sin_fecha_vale_hoy_sea_No_caduca_o_Sin_confirmar(EstadoVigenciaDocumento estado)
    {
        DocumentoEfectivo.ValidoHoy(estado, null, Hoy).Should().BeTrue();
    }

    [Fact]
    public void Si_solo_hay_vencidos_el_efectivo_es_el_de_emision_mas_reciente()
    {
        Orden(Nueva("vencido-antiguo", 400, venceEnDias: -30), Nueva("vencido-reciente", 100, venceEnDias: -2))
            .Should().Equal("vencido-reciente", "vencido-antiguo");
    }

    // ---------- 2. Nominativo antes que general (D3) ----------

    [Fact]
    public void El_nominativo_valido_gana_al_general_valido_aunque_el_general_sea_mas_reciente()
    {
        OrdenConNominativo(Nueva("general-reciente", 2), Nueva("nominativo-antiguo", 200, nominativo: true))
            .Should().Equal("nominativo-antiguo", "general-reciente");
    }

    [Fact]
    public void El_nominativo_vencido_no_gana_al_general_valido_porque_valido_hoy_va_primero()
    {
        OrdenConNominativo(Nueva("nominativo-vencido", 10, venceEnDias: -5, nominativo: true), Nueva("general-valido", 300))
            .Should().Equal("general-valido", "nominativo-vencido");
    }

    [Fact]
    public void Sin_esNominativo_la_rama_nominativa_es_inerte()
    {
        // Hasta la PR 6 nadie pasa esNominativo: el orden es el mismo con o sin la marca en los datos.
        Orden(Nueva("a", 50, nominativo: true), Nueva("b", 5, nominativo: false)).Should().Equal("b", "a");
    }

    // ---------- 3. Emisión más reciente ----------

    [Fact]
    public void La_emision_mas_reciente_gana_aunque_venza_antes()
    {
        Orden(Nueva("larga", emisionHaceDias: 90, venceEnDias: 900), Nueva("reciente", emisionHaceDias: 2, venceEnDias: 5))
            .Should().Equal("reciente", "larga");
    }

    [Fact]
    public void La_emision_manda_sobre_la_vigencia_no_caduca()
    {
        Orden(
                Nueva("no-caduca-antigua", 900, EstadoVigenciaDocumento.NoCaduca),
                Nueva("con-fecha-reciente", 3, venceEnDias: 30))
            .Should().Equal("con-fecha-reciente", "no-caduca-antigua");
    }

    [Fact]
    public void Sin_confirmar_compite_por_su_emision_como_cualquier_otra_copia()
    {
        Orden(
                Nueva("confirmada-antigua", 200, venceEnDias: 400),
                Nueva("sin-confirmar-reciente", 4, EstadoVigenciaDocumento.SinConfirmar))
            .Should().Equal("sin-confirmar-reciente", "confirmada-antigua");
    }

    // ---------- 4. Vigencia confirmada ----------

    [Fact]
    public void A_igual_emision_la_confirmada_precede_a_la_sin_confirmar()
    {
        Orden(
                Nueva("sin-confirmar", 10, EstadoVigenciaDocumento.SinConfirmar, altaMinutos: 90),
                Nueva("con-fecha", 10, venceEnDias: 1, altaMinutos: 0))
            .Should().Equal("con-fecha", "sin-confirmar");
    }

    // ---------- «Vence más tarde» ya no decide ----------

    [Fact]
    public void A_igual_emision_y_vigencia_confirmada_el_vencimiento_no_decide_gana_la_dada_de_alta_mas_tarde()
    {
        Orden(
                Nueva("vence-tarde-alta-temprano", 10, venceEnDias: 700, altaMinutos: 0),
                Nueva("vence-pronto-alta-tarde", 10, venceEnDias: 20, altaMinutos: 60),
                Nueva("no-caduca-alta-media", 10, EstadoVigenciaDocumento.NoCaduca, altaMinutos: 30))
            .Should().Equal("vence-pronto-alta-tarde", "no-caduca-alta-media", "vence-tarde-alta-temprano");
    }

    // ---------- 5 y 6. CreadoEnUtc e Id ----------

    [Fact]
    public void A_igual_emision_y_vigencia_gana_la_dada_de_alta_mas_tarde()
    {
        Orden(
                Nueva("alta-temprano", 10, altaMinutos: 0),
                Nueva("alta-tarde", 10, altaMinutos: 90),
                Nueva("alta-media", 10, altaMinutos: 30))
            .Should().Equal("alta-tarde", "alta-media", "alta-temprano");
    }

    [Fact]
    public void Con_todo_igual_decide_el_menor_Id_y_el_resultado_no_depende_del_orden_de_entrada()
    {
        var a = Nueva("id-1", 10, id: new Guid("00000000-0000-0000-0000-000000000001"));
        var b = Nueva("id-2", 10, id: new Guid("00000000-0000-0000-0000-000000000002"));
        var c = Nueva("id-3", 10, id: new Guid("00000000-0000-0000-0000-000000000003"));

        Orden(a, b, c).Should().Equal("id-1", "id-2", "id-3");
        Orden(c, b, a).Should().Equal("id-1", "id-2", "id-3");
        Orden(b, c, a).Should().Equal("id-1", "id-2", "id-3");
    }

    // ---------- Un efectivo por clave ----------

    [Fact]
    public void UnoPorClave_elige_el_efectivo_de_cada_grupo_sin_mezclar_claves()
    {
        var copias = new[]
        {
            Nueva("tipo1-vencido-reciente", 3, venceEnDias: -1),
            Nueva("tipo1-valido-antiguo", 200, venceEnDias: 90),
            Nueva("tipo2-unico", 50),
        };
        var clave = new Dictionary<string, int>
        {
            ["tipo1-vencido-reciente"] = 1,
            ["tipo1-valido-antiguo"] = 1,
            ["tipo2-unico"] = 2,
        };

        var efectivos = DocumentoEfectivo.UnoPorClave(
            copias, c => clave[c.Nombre], c => c.Estado, c => c.Vencimiento, c => c.Emision, c => c.Creado, c => c.Id, Hoy);

        efectivos.Should().HaveCount(2);
        efectivos[1].Nombre.Should().Be("tipo1-valido-antiguo");
        efectivos[2].Nombre.Should().Be("tipo2-unico");
    }
}
