using System.Reflection;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Tenants;

/// <summary>
/// Invariantes del Encargo de administración (D-8, 2026-10-08): se liga a una
/// operación externa, del Tenant propietario entero y vigente; la cláusula del
/// contrato es obligatoria; y el registro solo añade — la única transición es
/// retirarlo, una vez.
/// </summary>
public class EncargoAdministracionTests
{
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid ActorReal = Guid.NewGuid();
    private static readonly DateTime Ahora = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private const string Clausula = "Cláusula 7.2 del contrato de servicios";

    private static AsignacionOperacion OperacionExternaUniversal() =>
        AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-30), null, Ahora);

    private static EncargoAdministracion Registrado(
        AsignacionOperacion? operacion = null, DateTime? vigenciaHasta = null) =>
        EncargoAdministracion.Registrar(
            operacion ?? OperacionExternaUniversal(), Clausula, EncargoAdministracion.VersionTextoVigente,
            OrigenEncargoAdministracion.AdministradorPropio, ActorReal, Ahora, vigenciaHasta);

    [Fact]
    public void Nace_vigente_con_los_dos_Tenants_y_la_operacion_copiados_de_la_Asignacion_de_Operacion()
    {
        var operacion = OperacionExternaUniversal();

        var encargo = EncargoAdministracion.Registrar(
            operacion, "  " + Clausula + "  ", " 2026-10-09 ",
            OrigenEncargoAdministracion.AprovisionamientoDePlataforma, ActorReal, Ahora, vigenciaHasta: null);

        encargo.PropietarioTenantId.Should().Be(Propietario);
        encargo.OperadorTenantId.Should().Be(Operador);
        encargo.AsignacionOperacionId.Should().Be(operacion.Id);
        encargo.ClausulaContrato.Should().Be(Clausula, "la cláusula se guarda recortada");
        encargo.VersionTexto.Should().Be("2026-10-09");
        encargo.Origen.Should().Be(OrigenEncargoAdministracion.AprovisionamientoDePlataforma);
        encargo.RegistradoPorUsuarioId.Should().Be(ActorReal);
        encargo.RegistradoEnUtc.Should().Be(Ahora);
        encargo.VigenciaDesde.Should().Be(Ahora);
        encargo.VigenciaHasta.Should().BeNull();
        encargo.RetiradoPorUsuarioId.Should().BeNull();
        encargo.RetiradoEnUtc.Should().BeNull();
        encargo.EstaVigente(Ahora).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void La_clausula_del_contrato_es_obligatoria(string? clausula)
    {
        var registrar = () => EncargoAdministracion.Registrar(
            OperacionExternaUniversal(), clausula!, EncargoAdministracion.VersionTextoVigente,
            OrigenEncargoAdministracion.AdministradorPropio, ActorReal, Ahora, null);

        registrar.Should().Throw<ArgumentException>().WithParameterName("clausulaContrato");
    }

    [Fact]
    public void La_clausula_no_supera_su_longitud_maxima()
    {
        var registrar = (int longitud) => () => EncargoAdministracion.Registrar(
            OperacionExternaUniversal(), new string('x', longitud), EncargoAdministracion.VersionTextoVigente,
            OrigenEncargoAdministracion.AdministradorPropio, ActorReal, Ahora, null);

        registrar(EncargoAdministracion.LongitudMaximaClausula).Should().NotThrow();
        registrar(EncargoAdministracion.LongitudMaximaClausula + 1).Should().Throw<ArgumentException>()
            .WithParameterName("clausulaContrato");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void La_version_del_texto_es_obligatoria(string? version)
    {
        var registrar = () => EncargoAdministracion.Registrar(
            OperacionExternaUniversal(), Clausula, version!,
            OrigenEncargoAdministracion.AdministradorPropio, ActorReal, Ahora, null);

        registrar.Should().Throw<ArgumentException>().WithParameterName("versionTexto");
    }

    [Fact]
    public void Exige_quien_lo_registra()
    {
        var registrar = () => EncargoAdministracion.Registrar(
            OperacionExternaUniversal(), Clausula, EncargoAdministracion.VersionTextoVigente,
            OrigenEncargoAdministracion.AdministradorPropio, Guid.Empty, Ahora, null);

        registrar.Should().Throw<ArgumentException>().WithParameterName("registradoPorUsuarioId");
    }

    [Fact]
    public void No_se_encarga_sobre_la_operacion_raiz()
    {
        // La raíz es el Tenant propietario operándose a sí mismo: no hay
        // Operador CAE externo al que encargar nada.
        var raiz = AsignacionOperacion.Raiz(Propietario, ServicioCae.Outbound, Ahora.AddYears(-1), Ahora);

        var registrar = () => Registrado(raiz);

        registrar.Should().Throw<ArgumentException>().WithParameterName("operacion")
            .WithMessage("*operación externa*");
    }

    [Fact]
    public void No_se_encarga_sobre_una_operacion_interna()
    {
        var interna = AsignacionOperacion.Interna(
            Propietario, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()),
            Ahora.AddDays(-1), null, Ahora);

        var registrar = () => Registrado(interna);

        registrar.Should().Throw<ArgumentException>().WithParameterName("operacion")
            .WithMessage("*operación externa*");
    }

    [Fact]
    public void No_se_encarga_sobre_una_operacion_que_no_sea_del_Tenant_propietario_entero()
    {
        var acotada = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()),
            Ahora.AddDays(-1), null, Ahora);

        var registrar = () => Registrado(acotada);

        registrar.Should().Throw<ArgumentException>().WithParameterName("operacion")
            .WithMessage("*universal*");
    }

    [Fact]
    public void No_se_encarga_sobre_una_operacion_cerrada()
    {
        var cerrada = OperacionExternaUniversal();
        cerrada.Cerrar(MotivoCierreAsignacion.Revocada, Ahora.AddMinutes(-1));

        var registrar = () => Registrado(cerrada);

        registrar.Should().Throw<ArgumentException>().WithParameterName("operacion")
            .WithMessage("*no está vigente*");
    }

    [Fact]
    public void No_se_encarga_sobre_una_operacion_suspendida_ni_programada_ni_caducada()
    {
        var suspendida = OperacionExternaUniversal();
        suspendida.Suspender();
        var programada = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(1), null, Ahora);
        var caducada = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            Ahora.AddDays(-30), Ahora.AddDays(-1), Ahora.AddDays(-30));

        foreach (var operacion in new[] { suspendida, programada, caducada })
        {
            var registrar = () => Registrado(operacion);
            registrar.Should().Throw<ArgumentException>().WithParameterName("operacion")
                .WithMessage("*no está vigente*");
        }
    }

    [Fact]
    public void La_vigencia_si_tiene_fin_termina_despues_de_empezar()
    {
        var enElPasado = () => Registrado(vigenciaHasta: Ahora.AddSeconds(-1));
        var ahoraMismo = () => Registrado(vigenciaHasta: Ahora);

        enElPasado.Should().Throw<ArgumentException>().WithParameterName("vigenciaHasta");
        ahoraMismo.Should().Throw<ArgumentException>().WithParameterName("vigenciaHasta");
        Registrado(vigenciaHasta: Ahora.AddDays(1)).VigenciaHasta.Should().Be(Ahora.AddDays(1));
    }

    [Fact]
    public void La_vigencia_es_semiabierta_y_caduca_al_llegar_su_fin()
    {
        var encargo = Registrado(vigenciaHasta: Ahora.AddDays(10));

        encargo.EstaVigente(Ahora.AddSeconds(-1)).Should().BeFalse("todavía no había empezado");
        encargo.EstaVigente(Ahora).Should().BeTrue();
        encargo.EstaVigente(Ahora.AddDays(10).AddSeconds(-1)).Should().BeTrue();
        encargo.EstaVigente(Ahora.AddDays(10)).Should().BeFalse("el fin no pertenece a la vigencia");
    }

    [Fact]
    public void Retirarlo_lo_deja_sin_vigencia_y_anota_al_actor_real()
    {
        var encargo = Registrado();
        var quienRetira = Guid.NewGuid();

        encargo.Retirar(quienRetira, Ahora.AddDays(3));

        encargo.RetiradoPorUsuarioId.Should().Be(quienRetira);
        encargo.RetiradoEnUtc.Should().Be(Ahora.AddDays(3));
        encargo.EstaVigente(Ahora.AddDays(3)).Should().BeFalse();
        encargo.EstaVigente(Ahora.AddDays(1)).Should().BeFalse(
            "un encargo retirado no vale para ningún instante: la vigencia se pregunta para decidir ahora, no para reconstruir el pasado");
    }

    [Fact]
    public void Se_retira_una_sola_vez()
    {
        var encargo = Registrado();
        var primero = Guid.NewGuid();
        encargo.Retirar(primero, Ahora.AddDays(1));

        var otraVez = () => encargo.Retirar(Guid.NewGuid(), Ahora.AddDays(2));

        otraVez.Should().Throw<InvalidOperationException>().WithMessage("*ya estaba retirado*");
        encargo.RetiradoPorUsuarioId.Should().Be(primero, "la segunda llamada no reescribe quién lo retiró");
        encargo.RetiradoEnUtc.Should().Be(Ahora.AddDays(1));
    }

    [Fact]
    public void La_retirada_exige_autor()
    {
        var retirar = () => Registrado().Retirar(Guid.Empty, Ahora);

        retirar.Should().Throw<ArgumentException>().WithParameterName("actorRealUsuarioId");
    }

    /// <summary>
    /// Append-only, comprobado sobre la forma del tipo y no sobre una lista de
    /// casos: ninguna propiedad tiene <c>set</c> público, y de sus métodos
    /// públicos de instancia el único que cambia algún campo es
    /// <see cref="EncargoAdministracion.Retirar"/>. Un <c>CambiarClausula</c>
    /// o un <c>Reactivar</c> futuros ponen este test en rojo.
    /// </summary>
    [Fact]
    public void Es_append_only_ningun_miembro_publico_salvo_Retirar_cambia_su_estado()
    {
        var tipo = typeof(EncargoAdministracion);

        var propiedades = tipo.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        propiedades.Where(p => p.SetMethod is { IsPublic: true }).Select(p => p.Name)
            .Should().BeEmpty("ninguna propiedad del encargo se escribe desde fuera");

        var metodosPublicos = tipo
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();

        metodosPublicos.Should().Contain(nameof(EncargoAdministracion.Retirar),
            "control positivo: si la reflexión no ve Retirar, tampoco vería un mutador nuevo");
        metodosPublicos.Should().BeEquivalentTo(
            [nameof(EncargoAdministracion.Retirar), nameof(EncargoAdministracion.EstaVigente)],
            "la única transición es retirar; cualquier otro método público hay que justificarlo aquí");

        // EstaVigente es una pregunta: no cambia ni un campo.
        var encargo = Registrado(vigenciaHasta: Ahora.AddDays(5));
        var antes = Foto(encargo);
        _ = encargo.EstaVigente(Ahora);
        _ = encargo.EstaVigente(Ahora.AddYears(1));
        Foto(encargo).Should().Equal(antes);

        // Retirar sí cambia, y solo los dos campos de la retirada.
        encargo.Retirar(Guid.NewGuid(), Ahora.AddDays(1));
        var cambiados = Foto(encargo).Where(par => !Equals(antes[par.Key], par.Value)).Select(par => par.Key);
        cambiados.Should().BeEquivalentTo(
            [nameof(EncargoAdministracion.RetiradoPorUsuarioId), nameof(EncargoAdministracion.RetiradoEnUtc)]);
    }

    private static Dictionary<string, object?> Foto(EncargoAdministracion encargo) =>
        typeof(EncargoAdministracion)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(p => p.Name, p => p.GetValue(encargo));
}
