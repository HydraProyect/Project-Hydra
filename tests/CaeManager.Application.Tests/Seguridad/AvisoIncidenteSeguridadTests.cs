using CaeManager.Application.Common;
using CaeManager.Application.ContactosSeguridad;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Seguridad.Incidentes;
using CaeManager.Application.Seguridad.Incidentes.Aviso;
using CaeManager.Application.Seguridad.Incidentes.Aviso.Commands.ConfirmarYEnviarPrimerAviso;
using CaeManager.Application.Seguridad.Incidentes.Aviso.Commands.DecidirIncidenteSeguridad;
using CaeManager.Application.Seguridad.Incidentes.Aviso.Commands.EnviarNotificacionCompleta;
using CaeManager.Application.Seguridad.Incidentes.Aviso.Queries.ObtenerIncidenteSeguridad;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Tests.Seguridad;

/// <summary>
/// Aviso de violaciones de seguridad, incremento 3b: el Actor de Plataforma TALVEG confirma el incidente y envía
/// el primer aviso en un clic, y después la notificación completa, a cada Tenant beneficiario afectado. Aquí se
/// fija la barrera de Application (concesión AdminPlataforma global, fuera de una Sesión Privilegiada, con el
/// segundo factor activo y superado en la sesión), la idempotencia del envío y la plantilla. Las funciones
/// SECURITY DEFINER de la base repiten la concesión; eso lo prueba la integración bajo runtime.
/// </summary>
public class AvisoIncidenteSeguridadTests
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid TenantSinNadie = Guid.NewGuid();

    public enum Barrera
    {
        ConcesionAcotada,
        SinConcesion,
        EnSesionPrivilegiada,
        SinDobleFactor,
        SinTotpEnSesion,
    }

    // ── Autorización ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Barrera.ConcesionAcotada)]
    [InlineData(Barrera.SinConcesion)]
    [InlineData(Barrera.EnSesionPrivilegiada)]
    [InlineData(Barrera.SinDobleFactor)]
    [InlineData(Barrera.SinTotpEnSesion)]
    public async Task Sin_la_autoridad_completa_no_se_confirma_ni_se_envia_nada(Barrera barrera)
    {
        var (usuario, sesion, autorizacion, esperado) = Escenario(barrera);
        var plataforma = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Sospechado));
        var contactos = new ContactosFalsos(plataforma.Registro);
        var correo = new CorreoFalso();

        var primer = await new ConfirmarYEnviarPrimerAvisoCommandHandler(usuario, sesion, autorizacion, plataforma,
                contactos, correo, Opciones(), NullLogger<ConfirmarYEnviarPrimerAvisoCommandHandler>.Instance)
            .Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id, ContenidoCompleto()), default);
        var completa = await new EnviarNotificacionCompletaCommandHandler(usuario, sesion, autorizacion, plataforma,
                contactos, correo, Opciones(), NullLogger<EnviarNotificacionCompletaCommandHandler>.Instance)
            .Handle(new EnviarNotificacionCompletaCommand(plataforma.Incidente.Id, ContenidoCompleto()), default);
        var decidir = await new DecidirIncidenteSeguridadCommandHandler(usuario, sesion, autorizacion, plataforma)
            .Handle(new DecidirIncidenteSeguridadCommand(plataforma.Incidente.Id, DecisionIncidenteSeguridad.Descartar, "Falsa alarma"), default);

        primer.Error.Should().Be(esperado);
        completa.Error.Should().Be(esperado);
        decidir.Error.Should().Be(esperado);
        plataforma.Decisiones.Should().BeEmpty();
        plataforma.Reclamos.Should().BeEmpty();
        contactos.Consultas.Should().Be(0, "los destinatarios de otros Tenants no se leen sin autoridad");
        correo.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Con_concesion_global_segundo_factor_y_TOTP_se_confirma_y_sale_el_primer_aviso_a_cada_destinatario()
    {
        var plataforma = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Sospechado));
        var correo = new CorreoFalso();

        var resultado = await PrimerAviso(plataforma, correo)
            .Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id, ContenidoCompleto()), default);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().BeEquivalentTo(new ResumenEnvioAviso(3, 0, 0, 0, [TenantSinNadie]));
        plataforma.Decisiones.Should().ContainSingle().Which.Should().Be(DecisionIncidenteSeguridad.Confirmar);
        correo.Enviados.Select(e => e.Destinatario).Should().BeEquivalentTo(
            ["seguridad@a.test", "admin1@b.test", "admin2@b.test"]);
        correo.Enviados.Should().OnlyContain(e => e.Tipo == TipoAvisoCorreo.Seguridad && e.ResponderA == "seguridad@talveg.test");
        plataforma.Reclamos.Should().OnlyContain(r => r.Plantilla == PlantillaAvisoIncidenteSeguridad.PrimerAviso);
        plataforma.Envios.Values.Should().OnlyContain(e => e.Estado == EstadoEnvioAvisoIncidenteSeguridad.Enviado);
    }

    // ── Idempotencia y fallos ─────────────────────────────────────────────────

    [Fact]
    public async Task Repetir_el_clic_no_reenvia_lo_que_ya_salio_ni_cambia_la_hora_de_confirmacion()
    {
        var plataforma = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Sospechado));
        var correo = new CorreoFalso();
        var handler = PrimerAviso(plataforma, correo);

        await handler.Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id, ContenidoCompleto()), default);
        var confirmadoEn = plataforma.Incidente.ConfirmadoEnUtc;
        var segundo = await handler.Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id, ContenidoCompleto()), default);

        segundo.EsExitoso.Should().BeTrue();
        segundo.Valor.Enviados.Should().Be(0);
        segundo.Valor.YaEnviados.Should().Be(3);
        correo.Enviados.Should().HaveCount(3, "el reintento no repite ningún correo");
        plataforma.Incidente.ConfirmadoEnUtc.Should().Be(confirmadoEn);
    }

    [Fact]
    public async Task Un_envio_en_curso_por_otro_clic_se_deja_y_se_cuenta_aparte()
    {
        var plataforma = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Confirmado));
        plataforma.Envios[(PlantillaAvisoIncidenteSeguridad.PrimerAviso, "admin1@b.test")] =
            new EnvioFalso(Guid.NewGuid(), EstadoEnvioAvisoIncidenteSeguridad.Enviando, "asunto", "cuerpo");
        var correo = new CorreoFalso();

        var resultado = await PrimerAviso(plataforma, correo)
            .Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id, ContenidoCompleto()), default);

        resultado.Valor.EnCurso.Should().Be(1);
        resultado.Valor.Enviados.Should().Be(2);
        correo.Enviados.Select(e => e.Destinatario).Should().NotContain("admin1@b.test");
    }

    [Fact]
    public async Task Un_correo_que_falla_no_corta_los_demas_y_el_reintento_solo_reenvia_ese_con_el_texto_de_la_primera_vez()
    {
        var plataforma = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Sospechado));
        var correo = new CorreoFalso { FallaPara = { "seguridad@a.test" } };
        var handler = PrimerAviso(plataforma, correo);

        var primero = await handler.Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id, ContenidoCompleto()), default);

        primero.Valor.Enviados.Should().Be(2);
        primero.Valor.Fallidos.Should().Be(1);
        correo.Enviados.Select(e => e.Destinatario).Should().Contain(["admin1@b.test", "admin2@b.test"]);
        var fallido = plataforma.Envios[(PlantillaAvisoIncidenteSeguridad.PrimerAviso, "seguridad@a.test")];
        fallido.Estado.Should().Be(EstadoEnvioAvisoIncidenteSeguridad.Fallido);
        plataforma.Resoluciones.Should().Contain((fallido.Id, false, CorreoFalso.CodigoFallo));
        var cuerpoPrimeraVez = fallido.Cuerpo;

        correo.FallaPara.Clear();
        var editado = ContenidoCompleto() with { Naturaleza = "Texto cambiado después" };
        var reintento = await handler.Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id, editado), default);

        reintento.Valor.Enviados.Should().Be(1);
        reintento.Valor.YaEnviados.Should().Be(2);
        var reenviado = correo.Enviados.Last(e => e.Destinatario == "seguridad@a.test");
        reenviado.Cuerpo.Should().Be(cuerpoPrimeraVez);
        reenviado.Cuerpo.Should().NotContain("Texto cambiado después");
    }

    [Fact]
    public async Task Si_la_base_rechaza_un_destinatario_el_envio_se_detiene_con_el_error()
    {
        var plataforma = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Sospechado))
        {
            RechazaDestinatario = "admin1@b.test",
        };
        var correo = new CorreoFalso();

        var resultado = await PrimerAviso(plataforma, correo)
            .Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id, ContenidoCompleto()), default);

        resultado.Error.Should().Be(ErroresIncidenteSeguridad.DestinatarioNoValido);
        correo.Enviados.Select(e => e.Destinatario).Should().NotContain("admin1@b.test");
    }

    // ── Orden y estado ────────────────────────────────────────────────────────

    [Fact]
    public async Task Los_destinatarios_se_resuelven_antes_de_confirmar()
    {
        var plataforma = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Sospechado));

        await PrimerAviso(plataforma, new CorreoFalso())
            .Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id, ContenidoCompleto()), default);

        plataforma.Registro.Should().ContainInOrder("destinatarios", "decidir:Confirmar", "reclamar");
    }

    [Fact]
    public async Task Si_los_destinatarios_no_se_pueden_resolver_el_plazo_no_empieza()
    {
        var plataforma = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Sospechado));
        var contactos = new ContactosFalsos(plataforma.Registro) { Falla = true };
        var correo = new CorreoFalso();

        var resultado = await PrimerAviso(plataforma, correo, contactos)
            .Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id, ContenidoCompleto()), default);

        resultado.EsFallido.Should().BeTrue();
        plataforma.Decisiones.Should().BeEmpty();
        plataforma.Incidente.ConfirmadoEnUtc.Should().BeNull();
        correo.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_incidente_Descartado_no_se_confirma_ni_se_avisa()
    {
        var plataforma = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Descartado));
        var correo = new CorreoFalso();

        var primer = await PrimerAviso(plataforma, correo)
            .Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id, ContenidoCompleto()), default);
        var completa = await Completa(plataforma, correo)
            .Handle(new EnviarNotificacionCompletaCommand(plataforma.Incidente.Id, ContenidoCompleto()), default);

        primer.Error.Should().Be(ErroresIncidenteSeguridad.EstadoNoValido);
        completa.Error.Should().Be(ErroresIncidenteSeguridad.NoConfirmado);
        plataforma.Decisiones.Should().BeEmpty();
        plataforma.Reclamos.Should().BeEmpty();
        correo.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task La_notificacion_completa_exige_el_incidente_confirmado_y_los_cinco_apartados()
    {
        var sospechado = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Sospechado));
        (await Completa(sospechado, new CorreoFalso())
                .Handle(new EnviarNotificacionCompletaCommand(sospechado.Incidente.Id, ContenidoCompleto()), default))
            .Error.Should().Be(ErroresIncidenteSeguridad.NoConfirmado);

        var confirmado = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Confirmado));
        var correo = new CorreoFalso();
        (await Completa(confirmado, correo)
                .Handle(new EnviarNotificacionCompletaCommand(confirmado.Incidente.Id, ContenidoCompleto() with { Consecuencias = " " }), default))
            .Error.Should().Be(ErroresIncidenteSeguridad.ContenidoIncompleto);

        var resultado = await Completa(confirmado, correo)
            .Handle(new EnviarNotificacionCompletaCommand(confirmado.Incidente.Id, ContenidoCompleto()), default);
        resultado.Valor.Enviados.Should().Be(3);
        confirmado.Decisiones.Should().BeEmpty("la notificación completa no vuelve a confirmar");
        confirmado.Reclamos.Should().OnlyContain(r => r.Plantilla == PlantillaAvisoIncidenteSeguridad.NotificacionCompleta);
    }

    [Fact]
    public async Task El_primer_aviso_admite_las_consecuencias_en_evaluacion_pero_no_un_apartado_demasiado_largo()
    {
        var plataforma = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Sospechado));

        var largo = await PrimerAviso(plataforma, new CorreoFalso())
            .Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id,
                ContenidoCompleto() with { Medidas = new string('m', ContenidoAvisoIncidente.LongitudMaximaApartado + 1) }), default);
        largo.Error.Should().Be(ErroresIncidenteSeguridad.ContenidoDemasiadoLargo);
        plataforma.Decisiones.Should().BeEmpty();

        var sinConsecuencias = await PrimerAviso(plataforma, new CorreoFalso())
            .Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id, ContenidoCompleto() with { Consecuencias = "" }), default);
        sinConsecuencias.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Descartar_exige_motivo_y_Confirmar_no_va_por_la_decision_suelta()
    {
        var validador = new DecidirIncidenteSeguridadCommandValidator();
        validador.Validate(new DecidirIncidenteSeguridadCommand(Guid.NewGuid(), DecisionIncidenteSeguridad.Confirmar, null))
            .IsValid.Should().BeFalse();
        validador.Validate(new DecidirIncidenteSeguridadCommand(Guid.NewGuid(), DecisionIncidenteSeguridad.Descartar, "  "))
            .Errors.Should().Contain(e => e.PropertyName == nameof(DecidirIncidenteSeguridadCommand.Motivo));

        var plataforma = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Sospechado));
        var handler = new DecidirIncidenteSeguridadCommandHandler(UsuarioCompleto(), new SesionPrivilegiadaFalsa(null),
            AutorizacionAdminPlataformaFalsa.Global(), plataforma);

        (await handler.Handle(new DecidirIncidenteSeguridadCommand(plataforma.Incidente.Id, DecisionIncidenteSeguridad.Confirmar, null), default))
            .EsFallido.Should().BeTrue();
        (await handler.Handle(new DecidirIncidenteSeguridadCommand(plataforma.Incidente.Id, DecisionIncidenteSeguridad.Descartar, " "), default))
            .Error.Should().Be(ErroresIncidenteSeguridad.MotivoObligatorio);
        plataforma.Decisiones.Should().BeEmpty();

        (await handler.Handle(new DecidirIncidenteSeguridadCommand(plataforma.Incidente.Id, DecisionIncidenteSeguridad.Descartar, "  Falsa alarma  "), default))
            .EsExitoso.Should().BeTrue();
        plataforma.Decisiones.Should().ContainSingle().Which.Should().Be(DecisionIncidenteSeguridad.Descartar);
        plataforma.UltimoMotivo.Should().Be("Falsa alarma");
    }

    // ── Plantilla ─────────────────────────────────────────────────────────────

    [Fact]
    public void La_plantilla_escapa_el_HTML_de_cada_apartado_del_nombre_y_de_la_organizacion()
    {
        var contenido = new ContenidoAvisoIncidente(
            "<script>alert(1)</script>", "Datos & <b>mas</b>", "Consecuencias", "Medidas", "Contacto \"x\"");

        var cuerpo = PlantillaAvisoIncidente.Cuerpo(PlantillaAvisoIncidenteSeguridad.NotificacionCompleta, contenido,
            "INC-2026-ABCDEF", "Org <img src=x>", "Marta <i>", sinContactoValidado: false, DateTime.UtcNow);

        cuerpo.Should().NotContain("<script>").And.NotContain("<b>mas</b>").And.NotContain("<img").And.NotContain("Marta <i>");
        cuerpo.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
        cuerpo.Should().Contain("Datos &amp; &lt;b&gt;mas&lt;/b&gt;");
        cuerpo.Should().Contain("Org &lt;img src=x&gt;");
        cuerpo.Should().Contain("Contacto &quot;x&quot;");
    }

    [Fact]
    public void La_plantilla_dice_a_los_Administradores_que_les_llega_por_no_haber_contacto_validado()
    {
        var conContacto = PlantillaAvisoIncidente.Cuerpo(PlantillaAvisoIncidenteSeguridad.PrimerAviso, ContenidoCompleto(),
            "INC-2026-ABCDEF", "Org A", "Marta", sinContactoValidado: false, DateTime.UtcNow);
        var sinContacto = PlantillaAvisoIncidente.Cuerpo(PlantillaAvisoIncidenteSeguridad.PrimerAviso, ContenidoCompleto(),
            "INC-2026-ABCDEF", "Org A", "Marta", sinContactoValidado: true, DateTime.UtcNow);

        sinContacto.Should().Contain("como Administradores de Org A").And.Contain("no tiene un contacto de seguridad validado");
        conContacto.Should().NotContain("como Administradores");
    }

    [Fact]
    public void El_primer_aviso_omite_las_consecuencias_y_anuncia_la_completa_dentro_de_las_36_horas()
    {
        var confirmado = new DateTime(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);

        var primer = PlantillaAvisoIncidente.Cuerpo(PlantillaAvisoIncidenteSeguridad.PrimerAviso,
            ContenidoCompleto() with { Consecuencias = "NO-DEBE-SALIR" }, "INC-2026-ABCDEF", "Org A", "Marta", false, confirmado);
        var completa = PlantillaAvisoIncidente.Cuerpo(PlantillaAvisoIncidenteSeguridad.NotificacionCompleta,
            ContenidoCompleto() with { Consecuencias = "SI-SALE" }, "INC-2026-ABCDEF", "Org A", "Marta", false, confirmado);

        primer.Should().NotContain("NO-DEBE-SALIR");
        primer.Should().Contain((confirmado + PlazosAvisoIncidenteSeguridad.NotificacionCompleta).EnHoraPeninsular().ToString("dd/MM/yyyy"));
        completa.Should().Contain("SI-SALE");
    }

    [Fact]
    public async Task El_correo_al_Tenant_sin_contacto_validado_lleva_la_marca_y_el_de_con_contacto_no()
    {
        var plataforma = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Sospechado));
        var correo = new CorreoFalso();

        await PrimerAviso(plataforma, correo)
            .Handle(new ConfirmarYEnviarPrimerAvisoCommand(plataforma.Incidente.Id, ContenidoCompleto()), default);

        correo.Enviados.Single(e => e.Destinatario == "seguridad@a.test").Cuerpo.Should().NotContain("como Administradores");
        correo.Enviados.Where(e => e.Destinatario.EndsWith("@b.test")).Should()
            .OnlyContain(e => e.Cuerpo.Contains("como Administradores"));
        plataforma.Reclamos.Where(r => r.TenantAfectadoId == TenantB).Should().OnlyContain(r => r.SinContactoValidado);
    }

    // ── Consulta de la página ─────────────────────────────────────────────────

    [Fact]
    public async Task La_pagina_se_lee_con_concesion_global_y_avisa_de_lo_que_falta_para_decidir()
    {
        var plataforma = new IncidentesFalsos(Incidente(EstadoIncidenteSeguridad.Sospechado));
        var contactos = new ContactosFalsos(plataforma.Registro);

        var sinConcesion = await new ObtenerIncidenteSeguridadQueryHandler(UsuarioCompleto(), new SesionPrivilegiadaFalsa(null),
                AutorizacionAdminPlataformaFalsa.AcotadaA(TenantA), plataforma, contactos, Opciones())
            .Handle(new ObtenerIncidenteSeguridadQuery(plataforma.Incidente.Id), default);
        sinConcesion.Error.Should().Be(ErroresIncidenteSeguridad.SoloActorDePlataforma);

        var sinTotp = await new ObtenerIncidenteSeguridadQueryHandler(
                new CurrentUserServiceFalso(Usuario, tieneDobleFactorActivo: true, superoTotp: false),
                new SesionPrivilegiadaFalsa(null), AutorizacionAdminPlataformaFalsa.Global(), plataforma, contactos, Opciones())
            .Handle(new ObtenerIncidenteSeguridadQuery(plataforma.Incidente.Id), default);

        sinTotp.EsExitoso.Should().BeTrue();
        sinTotp.Valor.MotivoParaNoDecidir.Should().Be(ErroresIncidenteSeguridad.TotpNoVerificado);
        sinTotp.Valor.Tenants.Single(t => t.TenantId == TenantA).SinContactoValidado.Should().BeFalse();
        sinTotp.Valor.Tenants.Single(t => t.TenantId == TenantB).SinContactoValidado.Should().BeTrue();
        sinTotp.Valor.Tenants.Single(t => t.TenantId == TenantSinNadie).Destinatarios.Should().BeEmpty();
        sinTotp.Valor.PrimerAvisoVenceEnUtc.Should().BeNull("sin confirmar, el plazo no ha empezado");
    }

    // ── Dobles ────────────────────────────────────────────────────────────────

    private static (ICurrentUserService, ISesionPrivilegiadaActual, IAutorizacionAdminPlataforma, Error) Escenario(Barrera barrera) =>
        barrera switch
        {
            Barrera.ConcesionAcotada => (UsuarioCompleto(), new SesionPrivilegiadaFalsa(null),
                AutorizacionAdminPlataformaFalsa.AcotadaA(TenantA, TenantB, TenantSinNadie), ErroresIncidenteSeguridad.SoloActorDePlataforma),
            Barrera.SinConcesion => (UsuarioCompleto(), new SesionPrivilegiadaFalsa(null),
                AutorizacionAdminPlataformaFalsa.SinNada(), ErroresIncidenteSeguridad.SoloActorDePlataforma),
            Barrera.EnSesionPrivilegiada => (UsuarioCompleto(),
                new SesionPrivilegiadaFalsa(new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), TenantA, CapacidadPrivilegio.AdminPlataforma, null)),
                AutorizacionAdminPlataformaFalsa.Global(), ErroresIncidenteSeguridad.SoloActorDePlataforma),
            Barrera.SinDobleFactor => (new CurrentUserServiceFalso(Usuario, tieneDobleFactorActivo: false, superoTotp: true),
                new SesionPrivilegiadaFalsa(null), AutorizacionAdminPlataformaFalsa.Global(), ErroresIncidenteSeguridad.SinDobleFactor),
            Barrera.SinTotpEnSesion => (new CurrentUserServiceFalso(Usuario, tieneDobleFactorActivo: true, superoTotp: false),
                new SesionPrivilegiadaFalsa(null), AutorizacionAdminPlataformaFalsa.Global(), ErroresIncidenteSeguridad.TotpNoVerificado),
            _ => throw new ArgumentOutOfRangeException(nameof(barrera)),
        };

    private static CurrentUserServiceFalso UsuarioCompleto() =>
        new(Usuario, tieneDobleFactorActivo: true, superoTotp: true);

    private static IOptions<OpcionesIncidentesSeguridad> Opciones() =>
        Options.Create(new OpcionesIncidentesSeguridad { CorreoContactoSeguridadTalveg = " seguridad@talveg.test " });

    private static ContenidoAvisoIncidente ContenidoCompleto() =>
        new("Naturaleza del incidente", "Datos e interesados", "Consecuencias probables", "Medidas adoptadas", "Contacto de TALVEG");

    private static IncidenteSeguridadDto Incidente(EstadoIncidenteSeguridad estado) =>
        new(Guid.NewGuid(), SenalIncidenteSeguridad.InicioSesionNoReconocido, estado,
            DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1), 1, [TenantA, TenantB, TenantSinNadie], "{}",
            estado == EstadoIncidenteSeguridad.Confirmado ? DateTime.UtcNow.AddMinutes(-30) : null, [], []);

    private static ConfirmarYEnviarPrimerAvisoCommandHandler PrimerAviso(
        IncidentesFalsos plataforma, CorreoFalso correo, ContactosFalsos? contactos = null) =>
        new(UsuarioCompleto(), new SesionPrivilegiadaFalsa(null), AutorizacionAdminPlataformaFalsa.Global(), plataforma,
            contactos ?? new ContactosFalsos(plataforma.Registro), correo, Opciones(),
            NullLogger<ConfirmarYEnviarPrimerAvisoCommandHandler>.Instance);

    private static EnviarNotificacionCompletaCommandHandler Completa(IncidentesFalsos plataforma, CorreoFalso correo) =>
        new(UsuarioCompleto(), new SesionPrivilegiadaFalsa(null), AutorizacionAdminPlataformaFalsa.Global(), plataforma,
            new ContactosFalsos(plataforma.Registro), correo, Opciones(),
            NullLogger<EnviarNotificacionCompletaCommandHandler>.Instance);

    private sealed record EnvioFalso(Guid Id, EstadoEnvioAvisoIncidenteSeguridad Estado, string Asunto, string Cuerpo);

    /// <summary>
    /// Doble de la base: guarda una fila por (plantilla, destinatario) y reclama como las funciones SECURITY
    /// DEFINER (Enviado y Enviando no se reclaman; Fallido sí, con el asunto y el cuerpo guardados).
    /// </summary>
    private sealed class IncidentesFalsos(IncidenteSeguridadDto incidente) : IIncidentesSeguridadPlataforma
    {
        public IncidenteSeguridadDto Incidente { get; private set; } = incidente;
        public List<string> Registro { get; } = [];
        public List<DecisionIncidenteSeguridad> Decisiones { get; } = [];
        public string? UltimoMotivo { get; private set; }
        public List<EnvioAvisoSolicitado> Reclamos { get; } = [];
        public List<(Guid EnvioId, bool Enviado, string? Error)> Resoluciones { get; } = [];
        public Dictionary<(PlantillaAvisoIncidenteSeguridad, string), EnvioFalso> Envios { get; } = [];
        public string? RechazaDestinatario { get; init; }

        public Task<IncidenteSeguridadDto?> ObtenerAsync(Guid incidenteId, CancellationToken cancellationToken) =>
            Task.FromResult(incidenteId == Incidente.Id ? Incidente : null);

        public Task<IReadOnlyDictionary<Guid, string>> NombresTenantsAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>
            {
                [TenantA] = "Org A",
                [TenantB] = "Org B",
            });

        public Task<Result> DecidirAsync(Guid incidenteId, DecisionIncidenteSeguridad decision, string? motivo, CancellationToken cancellationToken)
        {
            Registro.Add($"decidir:{decision}");
            Decisiones.Add(decision);
            UltimoMotivo = motivo;
            Incidente = decision switch
            {
                DecisionIncidenteSeguridad.Confirmar => Incidente with
                {
                    Estado = EstadoIncidenteSeguridad.Confirmado,
                    ConfirmadoEnUtc = Incidente.ConfirmadoEnUtc ?? DateTime.UtcNow,
                },
                DecisionIncidenteSeguridad.Descartar => Incidente with { Estado = EstadoIncidenteSeguridad.Descartado },
                _ => Incidente with { Estado = EstadoIncidenteSeguridad.EnEvaluacion },
            };
            return Task.FromResult(Result.Exito());
        }

        public Task<Result<EnvioAvisoReclamado>> ReclamarEnvioAsync(EnvioAvisoSolicitado envio, CancellationToken cancellationToken)
        {
            Registro.Add("reclamar");
            Reclamos.Add(envio);
            if (envio.DestinatarioCorreo == RechazaDestinatario)
                return Task.FromResult(Result.Fallo<EnvioAvisoReclamado>(ErroresIncidenteSeguridad.DestinatarioNoValido));

            var clave = (envio.Plantilla, envio.DestinatarioCorreo);
            if (Envios.TryGetValue(clave, out var existente))
            {
                if (existente.Estado != EstadoEnvioAvisoIncidenteSeguridad.Fallido)
                    return Task.FromResult(Result.Exito(new EnvioAvisoReclamado(
                        existente.Id, false, existente.Estado, existente.Asunto, existente.Cuerpo)));
                Envios[clave] = existente with { Estado = EstadoEnvioAvisoIncidenteSeguridad.Enviando };
                return Task.FromResult(Result.Exito(new EnvioAvisoReclamado(
                    existente.Id, true, EstadoEnvioAvisoIncidenteSeguridad.Enviando, existente.Asunto, existente.Cuerpo)));
            }

            var nuevo = new EnvioFalso(Guid.NewGuid(), EstadoEnvioAvisoIncidenteSeguridad.Enviando, envio.Asunto, envio.Cuerpo);
            Envios[clave] = nuevo;
            return Task.FromResult(Result.Exito(new EnvioAvisoReclamado(nuevo.Id, true, nuevo.Estado, nuevo.Asunto, nuevo.Cuerpo)));
        }

        public Task<Result> ResolverEnvioAsync(Guid envioId, bool enviado, string? error, CancellationToken cancellationToken)
        {
            Resoluciones.Add((envioId, enviado, error));
            var clave = Envios.Single(e => e.Value.Id == envioId).Key;
            Envios[clave] = Envios[clave] with
            {
                Estado = enviado ? EstadoEnvioAvisoIncidenteSeguridad.Enviado : EstadoEnvioAvisoIncidenteSeguridad.Fallido,
            };
            return Task.FromResult(Result.Exito());
        }
    }

    /// <summary>TenantA con contacto validado, TenantB solo con dos Administradores y TenantSinNadie vacío.</summary>
    private sealed class ContactosFalsos(List<string> registro) : IContactosSeguridadPlataforma
    {
        public bool Falla { get; init; }
        public int Consultas { get; private set; }

        public Task<IReadOnlyList<ContactoSeguridadPropuestoDto>> ObtenerPropuestosAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ContactoSeguridadPropuestoDto>>([]);

        public Task<ContactoSeguridadDto?> ObtenerVigenteAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<ContactoSeguridadDto?>(null);

        public Task<Result> ResolverAsync(Guid contactoId, string? motivoRechazo, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Exito());

        public Task<Result<IReadOnlyList<DestinatariosAvisoSeguridadTenant>>> ResolverDestinatariosAsync(
            IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken)
        {
            Consultas++;
            registro.Add("destinatarios");
            if (Falla)
                return Task.FromResult(Result.Fallo<IReadOnlyList<DestinatariosAvisoSeguridadTenant>>(ErroresContactoSeguridad.SoloActorDePlataforma));
            return Task.FromResult(Result.Exito<IReadOnlyList<DestinatariosAvisoSeguridadTenant>>(
            [
                new(TenantA, false, [new("Marta Ruiz", "seguridad@a.test", null)]),
                new(TenantB, true, [new("Admin Uno", "admin1@b.test", null), new("Admin Dos", "admin2@b.test", null)]),
                new(TenantSinNadie, true, []),
            ]));
        }
    }

    private sealed class CorreoFalso : IEmailService
    {
        public const string CodigoFallo = "Correo.Fallo";

        public HashSet<string> FallaPara { get; } = [];
        public List<(string Destinatario, string Asunto, string Cuerpo, TipoAvisoCorreo Tipo, string? ResponderA)> Enviados { get; } = [];

        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default)
        {
            if (FallaPara.Contains(destinatarioEmail))
                return Task.FromResult(Result.Fallo(Error.Crear(CodigoFallo, "SMTP caído")));
            Enviados.Add((destinatarioEmail, asunto, cuerpoHtml, tipo, responderA));
            return Task.FromResult(Result.Exito());
        }
    }

    private sealed class SesionPrivilegiadaFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
    }
}
