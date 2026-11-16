using CaeManager.Domain.Cumplimiento.Subencargados;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Cumplimiento;

/// <summary>
/// #2010, D-7: un subencargado no se activa sin aviso y antes de 30 días, y la objeción resuelve su efecto con lo que
/// el subencargado admite.
/// </summary>
public class SubencargadoTests
{
    private static readonly DateTime Ahora = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Actor = Guid.NewGuid();

    private static Subencargado Nuevo(bool admiteExclusion = true) => new(
        "Proveedor de IA", "Lectura asistida de documentos", "UE; sin transferencia", admiteExclusion,
        admiteExclusion ? ConmutadorExclusionSubencargado.Ia : null, Actor, Ahora);

    [Fact]
    public void El_plazo_minimo_de_aviso_es_de_treinta_dias()
    {
        Subencargado.PlazoMinimoAviso.Should().Be(TimeSpan.FromDays(30));
    }

    [Fact]
    public void Nace_propuesto_y_al_avisar_fija_la_fecha_minima_a_treinta_dias()
    {
        var subencargado = Nuevo();
        subencargado.Estado.Should().Be(EstadoSubencargado.Propuesto);

        subencargado.MarcarAvisado(Ahora);

        subencargado.Estado.Should().Be(EstadoSubencargado.Avisado);
        subencargado.AvisadoEnUtc.Should().Be(Ahora);
        subencargado.FechaMinimaActivacionUtc.Should().Be(Ahora.AddDays(30));
    }

    [Fact]
    public void Solo_se_marca_avisado_una_vez()
    {
        var subencargado = Nuevo();
        subencargado.MarcarAvisado(Ahora);

        var otraVez = () => subencargado.MarcarAvisado(Ahora.AddDays(1));

        otraVez.Should().Throw<InvalidOperationException>();
        subencargado.AvisadoEnUtc.Should().Be(Ahora, "un segundo envío no mueve la fecha mínima");
    }

    [Fact]
    public void Sin_aviso_no_se_activa()
    {
        var subencargado = Nuevo();

        subencargado.MotivoParaNoActivar(Ahora.AddDays(60), 0).Should().Contain("no se ha avisado");
        var activar = () => subencargado.Activar(Ahora.AddDays(60), 0, Actor);
        activar.Should().Throw<InvalidOperationException>();
        subencargado.Estado.Should().Be(EstadoSubencargado.Propuesto);
    }

    [Fact]
    public void Antes_de_treinta_dias_no_se_activa_aunque_todos_los_Tenants_esten_avisados()
    {
        var subencargado = Nuevo();
        subencargado.MarcarAvisado(Ahora);

        var unMinutoAntes = Ahora.AddDays(30).AddMinutes(-1);
        subencargado.MotivoParaNoActivar(unMinutoAntes, 0).Should().Contain("30 días");
        var activar = () => subencargado.Activar(unMinutoAntes, 0, Actor);
        activar.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(1, "Un Tenant propietario")]
    [InlineData(3, "3 Tenants propietarios")]
    public void Con_algun_Tenant_sin_aviso_probado_no_se_activa_aunque_haya_vencido_el_plazo(int sinAviso, string texto)
    {
        var subencargado = Nuevo();
        subencargado.MarcarAvisado(Ahora);

        subencargado.MotivoParaNoActivar(Ahora.AddDays(31), sinAviso).Should().Contain(texto);
        var activar = () => subencargado.Activar(Ahora.AddDays(31), sinAviso, Actor);
        activar.Should().Throw<InvalidOperationException>();
        subencargado.Estado.Should().Be(EstadoSubencargado.Avisado);
    }

    [Fact]
    public void Con_el_plazo_vencido_y_todos_los_Tenants_avisados_se_activa()
    {
        var subencargado = Nuevo();
        subencargado.MarcarAvisado(Ahora);

        subencargado.MotivoParaNoActivar(Ahora.AddDays(30), 0).Should().BeNull("el día 30 exacto ya vale");
        subencargado.Activar(Ahora.AddDays(30), 0, Actor);

        subencargado.Estado.Should().Be(EstadoSubencargado.Activo);
        subencargado.ActivadoEnUtc.Should().Be(Ahora.AddDays(30));
        subencargado.ActivadoPorUsuarioId.Should().Be(Actor);
    }

    [Fact]
    public void Un_subencargado_activo_o_retirado_no_se_vuelve_a_activar()
    {
        var subencargado = Nuevo();
        subencargado.MarcarAvisado(Ahora);
        subencargado.Activar(Ahora.AddDays(30), 0, Actor);

        subencargado.MotivoParaNoActivar(Ahora.AddDays(40), 0).Should().Contain("activo");

        subencargado.Retirar("Fin del contrato con el proveedor", Ahora.AddDays(50));
        subencargado.Estado.Should().Be(EstadoSubencargado.Retirado);
        subencargado.MotivoParaNoActivar(Ahora.AddDays(60), 0).Should().Contain("retirado");
        var retirarOtraVez = () => subencargado.Retirar("Otra vez", Ahora.AddDays(61));
        retirarOtraVez.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void El_conmutador_acompana_a_la_exclusion_y_solo_a_ella()
    {
        var sinConmutador = () => new Subencargado("X", "Y", "Z", true, null, Actor, Ahora);
        var conConmutadorSinExclusion = () => new Subencargado("X", "Y", "Z", false, ConmutadorExclusionSubencargado.Ia, Actor, Ahora);
        var fechaLocal = () => new Subencargado("X", "Y", "Z", false, null, Actor, DateTime.Now);

        sinConmutador.Should().Throw<ArgumentException>();
        conConmutadorSinExclusion.Should().Throw<ArgumentException>();
        fechaLocal.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Solo_un_envio_Enviado_es_aviso_probado()
    {
        AvisoCambioSubencargado Aviso(FaseAvisoCambioSubencargado fase) =>
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), fase, Ahora, Actor);

        Aviso(FaseAvisoCambioSubencargado.Enviado).EsAvisoProbado.Should().BeTrue();
        Aviso(FaseAvisoCambioSubencargado.Preparado).EsAvisoProbado.Should().BeFalse();
        Aviso(FaseAvisoCambioSubencargado.NoConfirmado).EsAvisoProbado.Should().BeFalse();
    }

    [Fact]
    public void La_objecion_a_un_subencargado_excluible_excluye_al_Tenant_por_su_conmutador()
    {
        var subencargado = Nuevo(admiteExclusion: true);
        subencargado.MarcarAvisado(Ahora);

        var objecion = new ObjecionSubencargado(subencargado, Guid.NewGuid(), "  No queremos IA  ", Ahora.AddDays(2));

        objecion.Efecto.Should().Be(EfectoObjecionSubencargado.ExcluidoParaElTenant);
        objecion.Conmutador.Should().Be(ConmutadorExclusionSubencargado.Ia);
        objecion.Motivo.Should().Be("No queremos IA");
        objecion.SubencargadoId.Should().Be(subencargado.Id);
    }

    [Fact]
    public void La_objecion_a_un_subencargado_no_excluible_deja_la_resolucion_sin_penalizacion()
    {
        var subencargado = Nuevo(admiteExclusion: false);
        subencargado.MarcarAvisado(Ahora);

        var objecion = new ObjecionSubencargado(subencargado, Guid.NewGuid(), "Ubicación no aceptable", Ahora.AddDays(2));

        objecion.Efecto.Should().Be(EfectoObjecionSubencargado.ResolucionSinPenalizacionDisponible);
        objecion.Conmutador.Should().BeNull();
    }

    [Fact]
    public void No_se_objeta_un_subencargado_sin_avisar_ni_uno_retirado()
    {
        var propuesto = Nuevo();
        var objetarPropuesto = () => new ObjecionSubencargado(propuesto, Guid.NewGuid(), "Motivo", Ahora);
        objetarPropuesto.Should().Throw<InvalidOperationException>();

        var retirado = Nuevo();
        retirado.MarcarAvisado(Ahora);
        retirado.Retirar("Ya no se usa", Ahora.AddDays(1));
        var objetarRetirado = () => new ObjecionSubencargado(retirado, Guid.NewGuid(), "Motivo", Ahora.AddDays(2));
        objetarRetirado.Should().Throw<InvalidOperationException>();
    }
}
