using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Plataforma;

/// <summary>
/// Las invariantes del plano 3 (ADR-011 § 8). No son reglas de negocio
/// negociables: cada una cierra una forma concreta de convertir el acceso de
/// soporte en una puerta trasera permanente.
/// </summary>
public class PrivilegioDePlataformaTests
{
    private static readonly Guid Tecnico = Guid.NewGuid();
    private static readonly Guid TenantCliente = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();
    private static readonly DateTime Ahora = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    // ---------- alcance ----------

    [Fact]
    public void Una_concesion_acotada_cubre_su_tenant_y_no_los_demas()
    {
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora, null);

        concesion.CubreEn(TenantCliente, Ahora).Should().BeTrue();
        concesion.CubreEn(OtroTenant, Ahora).Should().BeFalse();
        concesion.EsAlcanceGlobal.Should().BeFalse();
    }

    [Fact]
    public void Una_concesion_acotada_sin_ningun_tenant_no_tiene_sentido_y_se_rechaza()
    {
        var crear = () => ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [], Ahora, null);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void El_alcance_global_solo_existe_para_la_administracion_de_plataforma()
    {
        // Un alcance global de LECTURA sería la cuenta de soporte omnipotente
        // que el principio de mínimo privilegio prohíbe: por eso la fábrica
        // global no admite elegir capacidad.
        var global = ConcesionPrivilegio.Global(Tecnico, Ahora, null);

        global.Capacidad.Should().Be(CapacidadPrivilegio.AdminPlataforma);
        global.EsAlcanceGlobal.Should().BeTrue();
        global.CubreEn(Guid.NewGuid(), Ahora).Should().BeTrue("administrar la plataforma alcanza a cualquier tenant");
    }

    // ---------- vigencia ----------

    [Fact]
    public void Una_concesion_caducada_deja_de_cubrir_aunque_el_tenant_siga_en_su_alcance()
    {
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora.AddDays(-10), Ahora.AddDays(-1));

        concesion.CubreEn(TenantCliente, Ahora).Should().BeFalse();
        concesion.HaExpiradoEn(Ahora).Should().BeTrue();
    }

    [Fact]
    public void Revocar_corta_el_acceso_y_no_alarga_la_ventana_hacia_el_futuro()
    {
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora.AddDays(-1), Ahora.AddDays(10));

        concesion.Revocar(Ahora);

        concesion.Estado.Should().Be(EstadoConcesionPrivilegio.Revocada);
        concesion.CubreEn(TenantCliente, Ahora).Should().BeFalse();
        // Si el fin futuro sobreviviera, una consulta histórica sobre mañana
        // devolvería como vigente algo que se revocó hoy.
        concesion.VigenciaHasta.Should().Be(Ahora);
        concesion.Invoking(c => c.Revocar(Ahora)).Should().Throw<InvalidOperationException>();
    }

    // ---------- apertura de sesión ----------

    [Fact]
    public void Abrir_una_sesion_exige_que_la_concesion_cubra_ese_tenant()
    {
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora, null);

        var abrir = () => SesionPrivilegiada.Abrir(
            concesion, OtroTenant, "INC-1234", Ahora, TimeSpan.FromHours(2));

        abrir.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Una_concesion_revocada_ya_no_abre_sesiones()
    {
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora.AddDays(-1), null);
        concesion.Revocar(Ahora);

        var abrir = () => SesionPrivilegiada.Abrir(
            concesion, TenantCliente, "INC-1234", Ahora, TimeSpan.FromHours(2));

        abrir.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Una_sesion_exige_motivo()
    {
        // Es lo que permite responder después "entramos el día X por la
        // incidencia Y", que es la pregunta que un cliente tiene derecho a
        // hacer sobre sus propios datos.
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora, null);

        var abrir = () => SesionPrivilegiada.Abrir(
            concesion, TenantCliente, "   ", Ahora, TimeSpan.FromHours(2));

        abrir.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Una_sesion_exige_ventana_positiva()
    {
        // Sin ventana finita, una sesión privilegiada es un acceso permanente
        // con otro nombre.
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora, null);

        var abrir = () => SesionPrivilegiada.Abrir(
            concesion, TenantCliente, "INC-1234", Ahora, TimeSpan.Zero);

        abrir.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Una_sesion_no_puede_superar_la_ventana_maxima()
    {
        // El techo ya existía en producción, pero en un validador de
        // FluentValidation del comando heredado: cualquier camino que no pasara
        // por ese comando podía construir la ventana que quisiera. Sube al
        // dominio para que una sesión más larga sea IRREPRESENTABLE, no solo
        // rechazada en un formulario.
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora, null);

        var abrir = () => SesionPrivilegiada.Abrir(
            concesion, TenantCliente, "INC-1234", Ahora,
            SesionPrivilegiada.VentanaMaxima + TimeSpan.FromSeconds(1));

        abrir.Should().Throw<ArgumentException>(
            "una ventana que exceda el techo por un solo segundo no debería poder existir, la cree quien la cree");
    }

    [Fact]
    public void La_ventana_maxima_exacta_si_se_admite()
    {
        // Control positivo del límite: el techo es inclusivo. Sin esto, el test
        // de arriba pasaría igual si el dominio rechazara toda ventana larga,
        // incluida la que sí debe permitirse.
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora, null);

        var sesion = SesionPrivilegiada.Abrir(
            concesion, TenantCliente, "INC-1234", Ahora, SesionPrivilegiada.VentanaMaxima);

        sesion.ExpiraEnUtc.Should().Be(Ahora + SesionPrivilegiada.VentanaMaxima);
    }

    /// <summary>
    /// DEC-43 fija el techo, literalmente, en 4 horas — no "lo que
    /// <c>VentanaMaxima</c> valga hoy". Los dos tests de arriba pinchan contra
    /// la constante y seguirían en verde si alguien la cambiara a otro valor
    /// por error; este pincha contra el literal que DEC-43 decidió, así que una
    /// regresión que mueva <c>VentanaMaxima</c> de 4 horas revienta aquí aunque
    /// los tests simbólicos no lo noten.
    /// </summary>
    [Fact]
    public void El_techo_de_DEC_43_son_cuatro_horas_absolutas_ni_una_mas()
    {
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora, null);

        var exactas = SesionPrivilegiada.Abrir(
            concesion, TenantCliente, "INC-1234", Ahora, TimeSpan.FromHours(4));
        exactas.ExpiraEnUtc.Should().Be(Ahora.AddHours(4));

        var masUnSegundo = () => SesionPrivilegiada.Abrir(
            concesion, TenantCliente, "INC-1234", Ahora, TimeSpan.FromHours(4) + TimeSpan.FromSeconds(1));
        masUnSegundo.Should().Throw<ArgumentException>(
            "cuatro horas y un segundo es exactamente la trampa que DEC-43 quiere cerrada");
    }

    // ---------- impersonación ----------

    [Fact]
    public void Solo_una_concesion_de_impersonacion_puede_simular_a_un_usuario()
    {
        // Simular bajo cualquier otra capacidad sería una impersonación
        // encubierta, sin la ceremonia que la acompaña.
        var soporte = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora, null);

        var abrir = () => SesionPrivilegiada.Abrir(
            soporte, TenantCliente, "INC-1234", Ahora, TimeSpan.FromHours(2), usuarioSimuladoId: Guid.NewGuid());

        abrir.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Con_la_capacidad_de_impersonacion_la_sesion_registra_a_quien_se_simula()
    {
        var simulado = Guid.NewGuid();
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.Impersonacion, [TenantCliente], Ahora, null);

        var sesion = SesionPrivilegiada.Abrir(
            concesion, TenantCliente, "Reproducir INC-1234", Ahora, TimeSpan.FromHours(1),
            usuarioSimuladoId: simulado, ticket: "INC-1234");

        sesion.UsuarioSimuladoId.Should().Be(simulado);
        sesion.TenantObjetivoId.Should().Be(TenantCliente);
        sesion.Ticket.Should().Be("INC-1234");
        sesion.EstaAbierta.Should().BeTrue();
    }

    // ---------- vida de la sesión ----------

    [Fact]
    public void Una_sesion_deja_de_valer_al_vencer_la_ventana_aunque_nadie_la_cierre()
    {
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora, null);

        var sesion = SesionPrivilegiada.Abrir(
            concesion, TenantCliente, "INC-1234", Ahora, TimeSpan.FromHours(2));

        sesion.EstaVigenteEn(Ahora).Should().BeTrue();
        sesion.EstaVigenteEn(Ahora.AddHours(1)).Should().BeTrue();
        sesion.EstaVigenteEn(Ahora.AddHours(2)).Should().BeFalse("la ventana es semiabierta");
        sesion.EstaVigenteEn(Ahora.AddDays(1)).Should().BeFalse();
    }

    [Fact]
    public void Cerrar_una_sesion_es_final()
    {
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora, null);
        var sesion = SesionPrivilegiada.Abrir(
            concesion, TenantCliente, "INC-1234", Ahora, TimeSpan.FromHours(2));

        sesion.Cerrar(Ahora.AddMinutes(15));

        sesion.EstaAbierta.Should().BeFalse();
        sesion.CerradaEnUtc.Should().Be(Ahora.AddMinutes(15));
        sesion.EstaVigenteEn(Ahora.AddMinutes(30)).Should().BeFalse();
        sesion.Invoking(s => s.Cerrar(Ahora.AddHours(1))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Conceder_no_es_acceder()
    {
        // La separación entre grant y uso es lo que hace que una cuenta de
        // soporte comprometida no abra ninguna puerta por sí sola: tener la
        // concesión no toca nada hasta que alguien abre una sesión con motivo
        // y ventana.
        var concesion = ConcesionPrivilegio.SobreTenants(
            Tecnico, CapacidadPrivilegio.SoporteLectura, [TenantCliente], Ahora, null,
            concedidaPorUsuarioId: Tecnico, motivoConcesion: "Soporte de guardia");

        concesion.Estado.Should().Be(EstadoConcesionPrivilegio.Vigente);
        concesion.ConcedidaPorUsuarioId.Should().Be(Tecnico, "la auto-concesión se admite, pero queda registrada");
        concesion.CubreEn(TenantCliente, Ahora).Should().BeTrue();

        // Y sin embargo no existe ninguna sesión: nadie ha entrado.
        concesion.TenantsAlcanzados.Should().ContainSingle().Which.TenantId.Should().Be(TenantCliente);
    }
}
