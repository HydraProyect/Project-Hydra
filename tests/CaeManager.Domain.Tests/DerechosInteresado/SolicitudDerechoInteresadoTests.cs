using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.DerechosInteresado;
using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Domain.Tests.DerechosInteresado;

/// <summary>
/// Invariantes de la solicitud de un derecho del interesado (#2002): transiciones válidas, plazo de un mes
/// prorrogable una sola vez, dentro del primer mes, hasta dos meses más con motivo, motivo obligatorio para denegar y nada resuelto sin
/// identidad verificada.
/// </summary>
public class SolicitudDerechoInteresadoTests
{
    private static readonly Guid Registrador = Guid.NewGuid();
    private static readonly Guid Tramitador = Guid.NewGuid();
    private static readonly DateOnly Hoy = new(2026, 11, 16);
    private static readonly DateTime Ahora = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);

    // Un instante del primer mes de la solicitud de Nueva() (recibida el 31-ene, plazo ordinario el 28-feb).
    private static readonly DateTime DentroDelPrimerMes = new(2026, 2, 10, 10, 0, 0, DateTimeKind.Utc);

    private static SolicitudDerechoInteresado Nueva(DateOnly? recepcion = null) =>
        SolicitudDerechoInteresado.Registrar(
            TipoDerechoInteresado.Supresion, CanalEntradaSolicitudDerecho.CorreoElectronico,
            recepcion ?? new DateOnly(2026, 1, 31), "  Paula Campos  ", null, "  Pide que se borren sus datos.  ",
            Registrador, Hoy, Ahora);

    private static SolicitudDerechoInteresado EnCurso()
    {
        var s = Nueva();
        s.VerificarIdentidad(Tramitador, Ahora);
        s.IniciarTramitacion(Tramitador, Ahora);
        return s;
    }

    [Fact]
    public void Registrar_deja_la_solicitud_Recibida_con_plazo_de_un_mes_y_el_registrador()
    {
        var s = Nueva();

        s.Estado.Should().Be(EstadoSolicitudDerecho.Recibida);
        s.NombreInteresado.Should().Be("Paula Campos");
        s.Descripcion.Should().Be("Pide que se borren sus datos.");
        // 31 de enero + 1 mes = 28 de febrero (fin de mes, como AddMonths y la base).
        s.PlazoOrdinario.Should().Be(new DateOnly(2026, 2, 28));
        s.PlazoVencimiento.Should().Be(s.PlazoOrdinario);
        // Plazo ordinario + 2 meses = 28 de abril; recepción + 3 meses daría el 30, un plazo más largo.
        s.PlazoMaximoProrrogable.Should().Be(new DateOnly(2026, 4, 28));
        s.RegistradaPorUsuarioId.Should().Be(Registrador);
        s.EstaProrrogada.Should().BeFalse();
        s.EstaCerrada.Should().BeFalse();
    }

    [Fact]
    public void Registrar_rechaza_recepcion_futura_nombre_vacio_trabajador_vacio_y_actor_vacio()
    {
        var crear = (DateOnly recepcion, string nombre, Guid? trabajador, Guid actor) => () =>
            SolicitudDerechoInteresado.Registrar(TipoDerechoInteresado.Acceso, CanalEntradaSolicitudDerecho.Otro,
                recepcion, nombre, trabajador, null, actor, Hoy, Ahora);

        crear(Hoy.AddDays(1), "Paula", null, Registrador).Should().Throw<ArgumentException>();
        crear(Hoy, "   ", null, Registrador).Should().Throw<ArgumentException>();
        crear(Hoy, "Paula", Guid.Empty, Registrador).Should().Throw<ArgumentException>();
        crear(Hoy, "Paula", null, Guid.Empty).Should().Throw<ArgumentException>();
        crear(Hoy, new string('a', SolicitudDerechoInteresado.LongitudMaximaNombreInteresado + 1), null, Registrador)
            .Should().Throw<ArgumentException>();
        crear(Hoy, "Paula", null, Registrador).Should().NotThrow();
    }

    [Fact]
    public void Registrar_rechaza_tipo_y_canal_desconocidos()
    {
        var tipo = () => SolicitudDerechoInteresado.Registrar((TipoDerechoInteresado)99,
            CanalEntradaSolicitudDerecho.Otro, Hoy, "Paula", null, null, Registrador, Hoy, Ahora);
        var canal = () => SolicitudDerechoInteresado.Registrar(TipoDerechoInteresado.Acceso,
            (CanalEntradaSolicitudDerecho)99, Hoy, "Paula", null, null, Registrador, Hoy, Ahora);

        tipo.Should().Throw<ArgumentOutOfRangeException>();
        canal.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void El_recorrido_completo_deja_el_actor_de_cada_paso()
    {
        var s = EnCurso();
        s.Resolver("  Datos suprimidos.  ", Tramitador, Ahora);

        s.Estado.Should().Be(EstadoSolicitudDerecho.Resuelta);
        s.IdentidadVerificadaPorUsuarioId.Should().Be(Tramitador);
        s.TramitacionIniciadaPorUsuarioId.Should().Be(Tramitador);
        s.CerradaPorUsuarioId.Should().Be(Tramitador);
        s.CerradaEnUtc.Should().Be(Ahora);
        s.Resultado.Should().Be("Datos suprimidos.");
        s.MotivoDenegacion.Should().BeNull();
        s.EstaCerrada.Should().BeTrue();
    }

    [Fact]
    public void Nada_se_resuelve_sin_identidad_verificada()
    {
        var recibida = Nueva();
        recibida.Invoking(s => s.Resolver(null, Tramitador, Ahora)).Should().Throw<InvalidOperationException>();

        var verificada = Nueva();
        verificada.VerificarIdentidad(Tramitador, Ahora);
        verificada.Invoking(s => s.Resolver(null, Tramitador, Ahora)).Should().Throw<InvalidOperationException>(
            "resolver exige EnCurso");

        recibida.Estado.Should().Be(EstadoSolicitudDerecho.Recibida);
        verificada.Estado.Should().Be(EstadoSolicitudDerecho.IdentidadVerificada);
    }

    [Fact]
    public void No_se_empieza_a_tramitar_sin_verificar_la_identidad_ni_se_verifica_dos_veces()
    {
        var s = Nueva();
        s.Invoking(x => x.IniciarTramitacion(Tramitador, Ahora)).Should().Throw<InvalidOperationException>();

        s.VerificarIdentidad(Tramitador, Ahora);
        s.Invoking(x => x.VerificarIdentidad(Tramitador, Ahora)).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Se_deniega_con_motivo_desde_cualquier_estado_abierto(int pasos)
    {
        var s = Nueva();
        if (pasos >= 1) s.VerificarIdentidad(Tramitador, Ahora);
        if (pasos >= 2) s.IniciarTramitacion(Tramitador, Ahora);

        s.Denegar("  No se pudo verificar la identidad.  ", Tramitador, Ahora);

        s.Estado.Should().Be(EstadoSolicitudDerecho.Denegada);
        s.MotivoDenegacion.Should().Be("No se pudo verificar la identidad.");
        s.CerradaPorUsuarioId.Should().Be(Tramitador);
        s.Resultado.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Denegar_sin_motivo_no_cambia_nada(string? motivo)
    {
        var s = Nueva();

        s.Invoking(x => x.Denegar(motivo!, Tramitador, Ahora)).Should().Throw<ArgumentException>();

        s.Estado.Should().Be(EstadoSolicitudDerecho.Recibida);
        s.CerradaEnUtc.Should().BeNull();
    }

    [Fact]
    public void Una_solicitud_cerrada_no_admite_mas_pasos()
    {
        var resuelta = EnCurso();
        resuelta.Resolver(null, Tramitador, Ahora);
        var denegada = Nueva();
        denegada.Denegar("Manifiestamente infundada.", Tramitador, Ahora);

        foreach (var s in new[] { resuelta, denegada })
        {
            s.Invoking(x => x.Denegar("otra vez", Tramitador, Ahora)).Should().Throw<InvalidOperationException>();
            s.Invoking(x => x.Resolver(null, Tramitador, Ahora)).Should().Throw<InvalidOperationException>();
            s.Invoking(x => x.VerificarIdentidad(Tramitador, Ahora)).Should().Throw<InvalidOperationException>();
            s.Invoking(x => x.Prorrogar(s.PlazoMaximoProrrogable, "motivo", Tramitador, DentroDelPrimerMes))
                .Should().Throw<InvalidOperationException>();
        }
    }

    [Fact]
    public void La_prorroga_lleva_el_plazo_hasta_dos_meses_mas_con_motivo_y_no_cambia_el_estado()
    {
        var s = Nueva();

        s.Prorrogar(new DateOnly(2026, 4, 28), "  Complejidad y número de solicitudes.  ", Tramitador, DentroDelPrimerMes);

        s.PlazoVencimiento.Should().Be(new DateOnly(2026, 4, 28));
        s.PlazoOrdinario.Should().Be(new DateOnly(2026, 2, 28), "el plazo ordinario no cambia");
        s.MotivoProrroga.Should().Be("Complejidad y número de solicitudes.");
        s.ProrrogadaPorUsuarioId.Should().Be(Tramitador);
        s.EstaProrrogada.Should().BeTrue();
        s.Estado.Should().Be(EstadoSolicitudDerecho.Recibida);
    }

    [Fact]
    public void Con_el_plazo_ordinario_vencido_no_se_prorroga()
    {
        // RGPD art. 12.3: la prórroga se informa dentro del primer mes. Nueva() venció el 28-feb y Ahora es noviembre.
        var s = Nueva();

        s.Invoking(x => x.Prorrogar(new DateOnly(2026, 4, 28), "Complejidad.", Tramitador, Ahora))
            .Should().Throw<InvalidOperationException>();

        s.EstaProrrogada.Should().BeFalse();
        s.PlazoVencimiento.Should().Be(s.PlazoOrdinario);
        s.MotivoProrroga.Should().BeNull();
    }

    // El borde se mide en día de negocio (hora peninsular), no en fecha UTC: el 28-feb a las 23:00 UTC ya es el 1-mar
    // en Madrid (CET, UTC+1).
    [Theory]
    [InlineData("2026-02-28T00:00:00Z", true)]
    [InlineData("2026-02-28T22:59:59Z", true)]
    [InlineData("2026-02-28T23:00:00Z", false)]
    [InlineData("2026-03-01T10:00:00Z", false)]
    public void La_prorroga_se_acuerda_como_tarde_el_dia_del_plazo_ordinario(string instanteUtc, bool admitida)
    {
        var ahora = DateTime.Parse(instanteUtc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        var s = Nueva();
        s.PlazoOrdinario.Should().Be(new DateOnly(2026, 2, 28), "control del borde");

        var prorrogar = () => s.Prorrogar(new DateOnly(2026, 3, 31), "Complejidad.", Tramitador, ahora);

        if (admitida)
        {
            prorrogar.Should().NotThrow();
            s.PlazoVencimiento.Should().Be(new DateOnly(2026, 3, 31));
        }
        else
        {
            prorrogar.Should().Throw<InvalidOperationException>();
            s.EstaProrrogada.Should().BeFalse();
        }
    }

    [Fact]
    public void La_prorroga_es_unica()
    {
        var s = Nueva();
        s.Prorrogar(new DateOnly(2026, 3, 15), "Primera.", Tramitador, DentroDelPrimerMes);

        s.Invoking(x => x.Prorrogar(new DateOnly(2026, 4, 28), "Segunda.", Tramitador, DentroDelPrimerMes))
            .Should().Throw<InvalidOperationException>();
        s.PlazoVencimiento.Should().Be(new DateOnly(2026, 3, 15));
    }

    [Fact]
    public void La_prorroga_no_pasa_de_dos_meses_ni_se_queda_en_el_plazo_ordinario_ni_va_sin_motivo()
    {
        var s = Nueva();

        s.Invoking(x => x.Prorrogar(new DateOnly(2026, 4, 29), "Motivo.", Tramitador, DentroDelPrimerMes))
            .Should().Throw<ArgumentException>("el plazo ordinario más dos meses es el máximo");
        s.Invoking(x => x.Prorrogar(s.PlazoOrdinario, "Motivo.", Tramitador, DentroDelPrimerMes))
            .Should().Throw<ArgumentException>("una prórroga que no alarga no es prórroga");
        s.Invoking(x => x.Prorrogar(new DateOnly(2026, 3, 31), "  ", Tramitador, DentroDelPrimerMes))
            .Should().Throw<ArgumentException>("prorrogar exige motivo escrito");

        s.EstaProrrogada.Should().BeFalse();
        s.PlazoVencimiento.Should().Be(s.PlazoOrdinario);
    }

    [Fact]
    public void Cada_paso_pide_su_accion_de_auditoria_y_el_registro_ninguna()
    {
        var s = Nueva();
        var propia = (IAccionAuditoriaPropia)s;
        propia.ConsumirAccionAuditoria().Should().BeNull("el registro queda como «Creado»");

        s.VerificarIdentidad(Tramitador, Ahora);
        propia.ConsumirAccionAuditoria().Should().Be(RegistroAuditoria.AccionIdentidadVerificada);
        propia.ConsumirAccionAuditoria().Should().BeNull("la acción se consume una vez");

        s.Prorrogar(new DateOnly(2026, 3, 31), "Motivo.", Tramitador, DentroDelPrimerMes);
        propia.ConsumirAccionAuditoria().Should().Be(RegistroAuditoria.AccionPlazoProrrogado);

        s.IniciarTramitacion(Tramitador, Ahora);
        propia.ConsumirAccionAuditoria().Should().Be(RegistroAuditoria.AccionTramitacionIniciada);

        s.Resolver(null, Tramitador, Ahora);
        propia.ConsumirAccionAuditoria().Should().Be(RegistroAuditoria.AccionSolicitudResuelta);

        var denegada = Nueva();
        denegada.Denegar("Motivo.", Tramitador, Ahora);
        ((IAccionAuditoriaPropia)denegada).ConsumirAccionAuditoria()
            .Should().Be(RegistroAuditoria.AccionSolicitudDenegada);
    }

    [Fact]
    public void Un_paso_sin_actor_real_no_se_da()
    {
        var s = Nueva();

        s.Invoking(x => x.VerificarIdentidad(Guid.Empty, Ahora)).Should().Throw<ArgumentException>();
        s.Estado.Should().Be(EstadoSolicitudDerecho.Recibida);
    }
}

/// <summary>Invariantes de la fila de concesión de una capacidad del Tenant (decisión D-3 del 2026-11-15).</summary>
public class ConcesionCapacidadTenantTests
{
    private static readonly DateTime Ahora = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Conceder_deja_la_concesion_vigente_con_quien_la_concede()
    {
        var usuario = Guid.NewGuid();
        var administrador = Guid.NewGuid();

        var c = ConcesionCapacidadTenant.Conceder(usuario, CapacidadTenant.DerechosDelInteresado, administrador, Ahora);

        c.EstaVigente.Should().BeTrue();
        c.UsuarioId.Should().Be(usuario);
        c.ConcedidaPorUsuarioId.Should().Be(administrador);
        ((IAccionAuditoriaPropia)c).ConsumirAccionAuditoria().Should().BeNull("la concesión queda como «Creado»");
    }

    [Fact]
    public void Revocar_deja_quien_y_cuando_y_no_se_revoca_dos_veces()
    {
        var administrador = Guid.NewGuid();
        var c = ConcesionCapacidadTenant.Conceder(Guid.NewGuid(), CapacidadTenant.DerechosDelInteresado, administrador, Ahora);

        c.Revocar(administrador, Ahora.AddDays(1));

        c.EstaVigente.Should().BeFalse();
        c.RevocadaPorUsuarioId.Should().Be(administrador);
        c.RevocadaEnUtc.Should().Be(Ahora.AddDays(1));
        ((IAccionAuditoriaPropia)c).ConsumirAccionAuditoria().Should().Be(RegistroAuditoria.AccionCapacidadRevocada);
        c.Invoking(x => x.Revocar(administrador, Ahora)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Conceder_rechaza_cuentas_vacias_y_capacidades_desconocidas()
    {
        var a = () => ConcesionCapacidadTenant.Conceder(Guid.Empty, CapacidadTenant.DerechosDelInteresado, Guid.NewGuid(), Ahora);
        var b = () => ConcesionCapacidadTenant.Conceder(Guid.NewGuid(), CapacidadTenant.DerechosDelInteresado, Guid.Empty, Ahora);
        var c = () => ConcesionCapacidadTenant.Conceder(Guid.NewGuid(), (CapacidadTenant)99, Guid.NewGuid(), Ahora);

        a.Should().Throw<ArgumentException>();
        b.Should().Throw<ArgumentException>();
        c.Should().Throw<ArgumentOutOfRangeException>();
    }
}
