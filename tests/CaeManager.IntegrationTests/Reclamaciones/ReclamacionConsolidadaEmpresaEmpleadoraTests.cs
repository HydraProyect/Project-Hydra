using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresaEmpleadora;
using CaeManager.Application.Reclamaciones.Eventos;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionAEmpresaEmpleadora;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Reclamaciones;

/// <summary>
/// Reclamación por acceso y reclamación preventiva consolidada a la Empresa empleadora (decisión de Chris, 2026-10-19),
/// contra PostgreSQL con el rol de runtime (RLS real) y dos Tenants más uno sin el documento de Empresa. Lo que se mide:
/// el reparto en lotes (uno por Delegación y otro para lo de la propia Empresa), el filtro de Delegación, la intersección
/// con lo que exige el Centro, el envío en un único correo, el fallo cerrado fuera de la Asignación de Cartera y que una
/// Empresa de otro Tenant no se ve ni se reclama. Ningún test manda correo: el envío termina en un doble de
/// <see cref="EnviarMensajeNuevoCommand"/> y el <see cref="IEmailService"/> no debe llamarse nunca.
/// </summary>
public class ReclamacionConsolidadaEmpresaEmpleadoraTests : IAsyncLifetime
{
    private const string CorreoSevilla = "sevilla@contratista.test";
    private const string CorreoMalaga = "malaga@contratista.test";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tenantSinPlan = Guid.NewGuid();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantAgenda = Guid.NewGuid();
    private readonly Guid _tenantSinBloqueo = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly List<CaeManagerDbContext> _contextosRuntime = [];

    private CaeManagerDbContext _propietario = null!;
    private Escenario _a = null!;
    private Escenario _b = null!;
    private Escenario _sinPlan = null!;
    private Escenario _plataforma = null!;
    private Escenario _agenda = null!;
    private EscenarioSinBloqueo _sinBloqueo = null!;

    /// <summary>Un Centro que exige sin marcar nada «bloquea el acceso»: Ana con el certificado vencido, Bea sin él y el Plan vencido.</summary>
    private sealed record EscenarioSinBloqueo(
        Guid EmpresaId, Guid CentroId, Guid AnaId, Guid BeaId, Guid TipoCertificadoId, Guid DocAnaVencidoId, Guid DocPlanVencidoId);

    /// <summary>Todo lo que siembra un Tenant: una Empresa con dos Delegaciones, dos Trabajadores y un Centro que les exige lo bloqueante.</summary>
    private sealed record Escenario(
        Guid EmpresaId, Guid CentroId, Guid SevillaId, Guid MalagaId, Guid AnaId, Guid BeaId,
        Guid TipoCertificadoId, Guid TipoPlanId,
        Guid DocAnaCertificadoId, Guid DocAnaFormacionId, Guid DocBeaCertificadoId, Guid? DocPlanId,
        PlataformaSembrada? Plataforma = null);

    /// <summary>Lo vigente en TALVEG (a 12 meses, fuera de la ventana de reclamación) que la plataforma del Centro aún no da por acreditado.</summary>
    private sealed record PlataformaSembrada(
        Guid TipoAltaId, Guid TipoPolizaId, Guid DocAltaAnaId, Guid DocAltaBeaId, Guid DocPolizaId);

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        _a = await SembrarAsync(_tenantA, conPlanDePrevencion: true);
        _b = await SembrarAsync(_tenantB, conPlanDePrevencion: true);
        _sinPlan = await SembrarAsync(_tenantSinPlan, conPlanDePrevencion: false);
        _plataforma = await SembrarAsync(_tenantPlataforma, conPlanDePrevencion: true, conPlataforma: true);
        _agenda = await SembrarAsync(_tenantAgenda, conPlanDePrevencion: true, conAgendaPorTipo: true);
        _sinBloqueo = await SembrarSinBloqueoAsync(_tenantSinBloqueo);
    }

    public async Task DisposeAsync()
    {
        foreach (var contexto in _contextosRuntime)
            await contexto.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    // ---------- Preventiva ----------

    [Fact]
    public async Task La_preventiva_reparte_lo_de_los_Trabajadores_por_Delegacion_y_lo_de_la_Empresa_en_su_propio_lote()
    {
        var arnes = new Arnes(Runtime(_tenantA), new AlcanceDatosServiceFalso());

        var resultado = await arnes.Preventiva.Handle(new ObtenerReclamacionPreventivaEmpresaQuery(_a.EmpresaId), CancellationToken.None);

        resultado.Lotes.Should().HaveCount(3);
        var deSevilla = resultado.Lotes.Single(l => l.EmpresaEmpleadora?.DelegacionEmpresaId == _a.SevillaId);
        deSevilla.Documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo([_a.DocAnaCertificadoId, _a.DocAnaFormacionId]);
        deSevilla.Destinatarios.Should().ContainSingle().Which.Email.Should().Be(CorreoSevilla);
        var deMalaga = resultado.Lotes.Single(l => l.EmpresaEmpleadora?.DelegacionEmpresaId == _a.MalagaId);
        deMalaga.Documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo([_a.DocBeaCertificadoId]);
        deMalaga.Destinatarios.Should().ContainSingle().Which.Email.Should().Be(CorreoMalaga);
        var deLaEmpresa = resultado.Lotes.Single(l => l.EmpresaEmpleadora is null);
        deLaEmpresa.TitularId.Should().Be(_a.EmpresaId);
        deLaEmpresa.Documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo([_a.DocPlanId!.Value]);

        resultado.DelegacionesEmpresa.Select(d => d.Id).Should().BeEquivalentTo([_a.SevillaId, _a.MalagaId]);
        var centro = resultado.Centros.Should().ContainSingle().Subject;
        centro.CentroId.Should().Be(_a.CentroId);
        centro.Trabajadores.Select(t => t.Id).Should().BeEquivalentTo([_a.AnaId, _a.BeaId]);
    }

    [Fact]
    public async Task El_filtro_de_Delegacion_deja_fuera_lo_de_otra_Delegacion_y_lo_de_la_propia_Empresa()
    {
        var arnes = new Arnes(Runtime(_tenantA), new AlcanceDatosServiceFalso());

        var resultado = await arnes.Preventiva.Handle(
            new ObtenerReclamacionPreventivaEmpresaQuery(_a.EmpresaId, DelegacionEmpresaId: _a.SevillaId), CancellationToken.None);

        var lote = resultado.Lotes.Should().ContainSingle().Subject;
        lote.EmpresaEmpleadora!.DelegacionEmpresaId.Should().Be(_a.SevillaId);
        lote.Documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo([_a.DocAnaCertificadoId, _a.DocAnaFormacionId]);
        lote.Documentos.Should().NotContain(d => d.DocumentoId == _a.DocBeaCertificadoId || d.DocumentoId == _a.DocPlanId,
            "lo de Málaga y lo de la Empresa no son de la Delegación elegida");
        resultado.DelegacionesEmpresa.Should().HaveCount(2, "el filtro no recorta las opciones del propio filtro");
    }

    // ---------- Por acceso ----------

    [Fact]
    public async Task Por_acceso_devuelve_en_un_unico_lote_lo_que_el_Centro_exige_de_la_Empresa_y_del_Trabajador_elegido()
    {
        var arnes = new Arnes(Runtime(_tenantA), new AlcanceDatosServiceFalso());

        var lotes = await arnes.PorAcceso.Handle(
            new ObtenerReclamacionPorAccesoQuery(_a.EmpresaId, _a.CentroId, [_a.AnaId]), CancellationToken.None);

        var lote = lotes.Should().ContainSingle("lo de la Empresa viaja en el lote del único Trabajador").Subject;
        lote.EmpresaEmpleadora!.DelegacionEmpresaId.Should().Be(_a.SevillaId);
        lote.Destinatarios.Should().ContainSingle().Which.Email.Should().Be(CorreoSevilla);
        lote.Documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo([_a.DocAnaCertificadoId, _a.DocPlanId!.Value]);
        lote.Documentos.Should().NotContain(d => d.DocumentoId == _a.DocAnaFormacionId, "el Centro no exige la formación");
        lote.Documentos.Should().NotContain(d => d.DocumentoId == _a.DocBeaCertificadoId, "Bea no es la Trabajadora elegida");
    }

    [Fact]
    public async Task Por_acceso_con_Trabajadores_de_dos_Delegaciones_deja_lo_de_la_Empresa_en_su_propia_tarjeta()
    {
        var arnes = new Arnes(Runtime(_tenantA), new AlcanceDatosServiceFalso());

        var lotes = await arnes.PorAcceso.Handle(
            new ObtenerReclamacionPorAccesoQuery(_a.EmpresaId, _a.CentroId, [_a.AnaId, _a.BeaId]), CancellationToken.None);

        lotes.Should().HaveCount(3);
        lotes.Single(l => l.EmpresaEmpleadora?.DelegacionEmpresaId == _a.SevillaId).Documentos
            .Select(d => d.DocumentoId).Should().BeEquivalentTo([_a.DocAnaCertificadoId]);
        lotes.Single(l => l.EmpresaEmpleadora?.DelegacionEmpresaId == _a.MalagaId).Documentos
            .Select(d => d.DocumentoId).Should().BeEquivalentTo([_a.DocBeaCertificadoId]);
        lotes.Single(l => l.EmpresaEmpleadora is null).Documentos
            .Select(d => d.DocumentoId).Should().BeEquivalentTo([_a.DocPlanId!.Value]);
    }

    [Fact]
    public async Task Por_acceso_el_lote_fusionado_con_lo_de_la_Empresa_ofrece_tambien_al_contacto_dueno_del_Tipo_de_la_Empresa()
    {
        var arnes = new Arnes(Runtime(_tenantAgenda), new AlcanceDatosServiceFalso());

        var lotes = await arnes.PorAcceso.Handle(
            new ObtenerReclamacionPorAccesoQuery(_agenda.EmpresaId, _agenda.CentroId, [_agenda.AnaId]), CancellationToken.None);

        var lote = lotes.Should().ContainSingle().Subject;
        lote.EmpresaEmpleadora!.DelegacionEmpresaId.Should().BeNull("Ana no tiene Delegación: el lote va a la agenda de la Empresa");
        lote.Documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo([_agenda.DocAnaCertificadoId, _agenda.DocPlanId!.Value]);
        lote.Destinatarios!.Select(d => d.Email).Should().BeEquivalentTo(
            ["agenda@contratista.test", "plan@contratista.test"],
            "el predeterminado recibe el certificado y la dueña del Plan recibe lo de la Empresa, que ya viaja en este lote");
    }

    [Fact]
    public async Task Por_acceso_un_Trabajador_fuera_de_los_Trabajadores_visibles_no_aparece_ni_arrastra_lo_de_la_Empresa()
    {
        var soloBea = new Arnes(Runtime(_tenantA), new AlcanceDatosServiceFalso(trabajadorIds: [_a.BeaId]));

        var soloAna = await soloBea.PorAcceso.Handle(
            new ObtenerReclamacionPorAccesoQuery(_a.EmpresaId, _a.CentroId, [_a.AnaId]), CancellationToken.None);
        soloAna.Should().BeEmpty("Ana no está en la cartera de Trabajadores: ni lo suyo ni el Plan de la Empresa que el Centro exige por ella");

        var ambas = await soloBea.PorAcceso.Handle(
            new ObtenerReclamacionPorAccesoQuery(_a.EmpresaId, _a.CentroId, [_a.AnaId, _a.BeaId]), CancellationToken.None);
        var lote = ambas.Should().ContainSingle().Subject;
        lote.Documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo([_a.DocBeaCertificadoId, _a.DocPlanId!.Value]);
    }

    [Fact]
    public async Task Por_acceso_reclama_lo_exigido_vencido_y_pendiente_aunque_el_Centro_no_marque_nada_como_bloqueante()
    {
        // El caso de la siembra del piloto (2026-10-21): el Centro exige el certificado y el Plan sin marcar «bloquea el
        // acceso»; Ana lo tiene vencido, a Bea le falta y el Plan de la Empresa está vencido. Antes salía «al día».
        var s = _sinBloqueo;
        var arnes = new Arnes(Runtime(_tenantSinBloqueo), new AlcanceDatosServiceFalso());

        var lotes = await arnes.PorAcceso.Handle(
            new ObtenerReclamacionPorAccesoQuery(s.EmpresaId, s.CentroId, [s.AnaId, s.BeaId]), CancellationToken.None);

        var lote = lotes.Should().ContainSingle("sin Delegaciones todo va a la agenda de la Empresa, en un solo borrador").Subject;
        lote.Documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo([s.DocAnaVencidoId, s.DocPlanVencidoId]);
        lote.Documentos.Single(d => d.DocumentoId == s.DocAnaVencidoId).Estado.Should().Be(EstadoDocumento.Vencido);
        lote.PendientesSinFecha.Should().ContainSingle(p => p.TrabajadorId == s.BeaId && p.TipoDocumentoId == s.TipoCertificadoId,
            "a Bea le falta el certificado que el Centro exige");
        lote.Destinatarios.Should().ContainSingle().Which.Email.Should().Be("agenda@contratista.test");

        // Fallo cerrado: un Centro fuera del alcance de quien pregunta no exige nada, tampoco lo de la Empresa.
        var sinElCentro = new Arnes(Runtime(_tenantSinBloqueo), new AlcanceDatosServiceFalso(centroIds: [Guid.NewGuid()]));
        (await sinElCentro.PorAcceso.Handle(
            new ObtenerReclamacionPorAccesoQuery(s.EmpresaId, s.CentroId, [s.AnaId, s.BeaId]), CancellationToken.None)).Should().BeEmpty();
    }

    // ---------- Envío ----------

    [Fact]
    public async Task El_envio_por_acceso_con_un_documento_de_la_Empresa_y_otro_del_Trabajador_registra_un_unico_envio()
    {
        var conversacionId = await SembrarBuzonYConversacionAsync(_tenantA, _a.EmpresaId);
        var contexto = Runtime(_tenantA);
        var arnes = new Arnes(contexto, new AlcanceDatosServiceFalso(), conversacionId);

        var resultado = await arnes.Envio.Handle(
            new EnviarReclamacionEmpresaEmpleadoraCommand(_a.EmpresaId, _a.SevillaId, [_a.DocAnaCertificadoId, _a.DocPlanId!.Value]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : null);
        arnes.Mediador.Envios.Should().ContainSingle("un solo correo, con lo de la Empresa y lo del Trabajador");
        arnes.Mediador.Envios[0].Destinatarios.Should().BeEquivalentTo([CorreoSevilla]);
        arnes.Mediador.Envios[0].EmpresaId.Should().Be(_a.EmpresaId);
        arnes.Mediador.Publicados.OfType<ReclamacionEnviadaEvent>().Should().ContainSingle();
        arnes.Correo.Enviados.Should().BeEmpty("el envío real lo hace EnviarMensajeNuevoCommand, que aquí es un doble");

        var reclamaciones = await contexto.ReclamacionesDocumentales.Include(r => r.Documentos).ToListAsync();
        var reclamacion = reclamaciones.Should().ContainSingle().Subject;
        reclamacion.EmpresaId.Should().Be(_a.EmpresaId);
        reclamacion.ClienteId.Should().BeNull();
        reclamacion.DestinatarioEmail.Should().Be(CorreoSevilla);
        reclamacion.Documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo([_a.DocAnaCertificadoId, _a.DocPlanId!.Value]);
    }

    [Fact]
    public async Task El_envio_por_acceso_que_pide_tambien_un_documento_ausente_de_la_Empresa_se_registra()
    {
        // El Plan de prevención falta en la Empresa y el Centro lo exige: la reclamación por acceso lo ofrece como «Ausente» sin
        // Trabajador, y viaja en el mismo correo que lo del Trabajador. El registro (titular Empresa, documentos de Trabajador)
        // tiene que admitirlo.
        var conversacionId = await SembrarBuzonYConversacionAsync(_tenantSinPlan, _sinPlan.EmpresaId);
        var contexto = Runtime(_tenantSinPlan);
        var arnes = new Arnes(contexto, new AlcanceDatosServiceFalso(), conversacionId);

        var lotes = await arnes.PorAcceso.Handle(
            new ObtenerReclamacionPorAccesoQuery(_sinPlan.EmpresaId, _sinPlan.CentroId, [_sinPlan.AnaId]), CancellationToken.None);
        var lote = lotes.Should().ContainSingle().Subject;
        var ausente = lote.PendientesSinFecha.Should().ContainSingle(p => p.TrabajadorId == null).Subject;
        ausente.TipoDocumentoId.Should().Be(_sinPlan.TipoPlanId);

        var resultado = await arnes.Envio.Handle(
            new EnviarReclamacionEmpresaEmpleadoraCommand(
                _sinPlan.EmpresaId, _sinPlan.SevillaId, [_sinPlan.DocAnaCertificadoId], Pendientes: [ausente.ComoPedido()]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : null);
        arnes.Mediador.Envios.Should().ContainSingle();
        var reclamacion = (await contexto.ReclamacionesDocumentales.Include(r => r.Documentos).ToListAsync()).Should().ContainSingle().Subject;
        reclamacion.Documentos.Should().Contain(d => d.DocumentoId == _sinPlan.DocAnaCertificadoId);
        reclamacion.Documentos.Should().Contain(d => d.DocumentoId == null && d.TipoDocumentoId == _sinPlan.TipoPlanId && d.TrabajadorId == null);
    }

    // ---------- Fuera de cartera ----------

    [Fact]
    public async Task Fuera_de_la_Asignacion_de_Cartera_las_dos_consultas_devuelven_vacio_y_el_envio_se_rechaza()
    {
        var conversacionId = await SembrarBuzonYConversacionAsync(_tenantA, _a.EmpresaId);
        var contexto = Runtime(_tenantA);
        var fueraDeCartera = new AlcanceDatosServiceFalso(empresaIds: [Guid.NewGuid()]);
        var arnes = new Arnes(contexto, fueraDeCartera, conversacionId);

        var preventiva = await arnes.Preventiva.Handle(new ObtenerReclamacionPreventivaEmpresaQuery(_a.EmpresaId), CancellationToken.None);
        var porAcceso = await arnes.PorAcceso.Handle(
            new ObtenerReclamacionPorAccesoQuery(_a.EmpresaId, _a.CentroId, [_a.AnaId]), CancellationToken.None);
        var envio = await arnes.Envio.Handle(
            new EnviarReclamacionEmpresaEmpleadoraCommand(_a.EmpresaId, _a.SevillaId, [_a.DocAnaCertificadoId, _a.DocPlanId!.Value]),
            CancellationToken.None);

        preventiva.Lotes.Should().BeEmpty();
        preventiva.DelegacionesEmpresa.Should().BeEmpty();
        preventiva.Oficios.Should().BeEmpty();
        preventiva.Centros.Should().BeEmpty();
        porAcceso.Should().BeEmpty();
        envio.EsFallido.Should().BeTrue();
        envio.Error.Codigo.Should().Be("Reclamacion.SinAcceso");
        arnes.Mediador.Envios.Should().BeEmpty();
        (await contexto.ReclamacionesDocumentales.CountAsync()).Should().Be(0);
    }

    // ---------- Otro Tenant ----------

    [Fact]
    public async Task Una_Empresa_de_otro_Tenant_no_se_ve_ni_se_reclama()
    {
        var conversacionId = await SembrarBuzonYConversacionAsync(_tenantA, _a.EmpresaId);
        var contextoA = Runtime(_tenantA);
        var comoA = new Arnes(contextoA, new AlcanceDatosServiceFalso(), conversacionId);

        // Control positivo: el Tenant B ve lo suyo, así que el vacío de abajo es aislamiento y no una siembra incompleta.
        var deB = await new Arnes(Runtime(_tenantB), new AlcanceDatosServiceFalso())
            .Preventiva.Handle(new ObtenerReclamacionPreventivaEmpresaQuery(_b.EmpresaId), CancellationToken.None);
        deB.Lotes.Should().HaveCount(3);

        var preventiva = await comoA.Preventiva.Handle(new ObtenerReclamacionPreventivaEmpresaQuery(_b.EmpresaId), CancellationToken.None);
        var porAcceso = await comoA.PorAcceso.Handle(
            new ObtenerReclamacionPorAccesoQuery(_b.EmpresaId, _b.CentroId, [_b.AnaId, _b.BeaId]), CancellationToken.None);
        var envio = await comoA.Envio.Handle(
            new EnviarReclamacionEmpresaEmpleadoraCommand(_b.EmpresaId, _b.SevillaId, [_b.DocAnaCertificadoId, _b.DocPlanId!.Value]),
            CancellationToken.None);

        preventiva.Lotes.Should().BeEmpty();
        preventiva.DelegacionesEmpresa.Should().BeEmpty();
        preventiva.Centros.Should().BeEmpty();
        porAcceso.Should().BeEmpty();
        envio.EsFallido.Should().BeTrue();
        comoA.Mediador.Envios.Should().BeEmpty();
        (await contextoA.ReclamacionesDocumentales.CountAsync()).Should().Be(0);
    }

    // ---------- Pendiente de subir a plataformas (va aparte de lo reclamable) ----------

    [Fact]
    public async Task Lo_vigente_en_TALVEG_pero_sin_acreditar_en_la_plataforma_sale_aparte_y_no_en_los_lotes_de_reclamacion()
    {
        var p = _plataforma.Plataforma!;
        var arnes = new Arnes(Runtime(_tenantPlataforma), new AlcanceDatosServiceFalso());

        var pendientes = await arnes.PendientesDePlataforma.Handle(
            new ObtenerPendientesDeSubirAPlataformaEmpresaQuery(_plataforma.EmpresaId), CancellationToken.None);

        pendientes.Should().HaveCount(3);
        pendientes.Single(f => f.TipoDocumentoId == p.TipoAltaId && f.TrabajadorId == _plataforma.AnaId)
            .Situacion.Should().Be(SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma);
        pendientes.Single(f => f.TipoDocumentoId == p.TipoAltaId && f.TrabajadorId == _plataforma.BeaId)
            .Situacion.Should().Be(SituacionDeRequisitoBloqueante.SinValidarEnPlataforma);
        var deLaEmpresa = pendientes.Single(f => f.TipoDocumentoId == p.TipoPolizaId);
        deLaEmpresa.Ambito.Should().Be(AmbitoAplicacion.Empresa);
        deLaEmpresa.EmpresaId.Should().Be(_plataforma.EmpresaId);
        deLaEmpresa.Situacion.Should().Be(SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma);
        pendientes.Should().OnlyContain(f => f.CentroId == _plataforma.CentroId);

        // Los lotes de las dos reclamaciones parten solo de la vigencia en TALVEG: nada de lo pendiente en la plataforma entra.
        var idsPlataforma = new[] { p.DocAltaAnaId, p.DocAltaBeaId, p.DocPolizaId };
        var preventiva = await arnes.Preventiva.Handle(new ObtenerReclamacionPreventivaEmpresaQuery(_plataforma.EmpresaId), CancellationToken.None);
        preventiva.Lotes.Should().HaveCount(3, "la preventiva queda como sin plataforma: lo mismo de siempre");
        preventiva.Lotes.SelectMany(l => l.Documentos.Select(d => d.DocumentoId)).Should().NotIntersectWith(idsPlataforma);
        preventiva.Lotes.SelectMany(l => (l.PendientesSinFecha ?? []).Select(d => d.TipoDocumentoId)).Should().NotContain([p.TipoAltaId, p.TipoPolizaId]);

        var porAcceso = await arnes.PorAcceso.Handle(
            new ObtenerReclamacionPorAccesoQuery(_plataforma.EmpresaId, _plataforma.CentroId, [_plataforma.AnaId, _plataforma.BeaId]), CancellationToken.None);
        porAcceso.Should().NotBeEmpty("el Centro sigue exigiendo el certificado y el plan, que sí están en la ventana");
        porAcceso.SelectMany(l => l.Documentos.Select(d => d.DocumentoId)).Should().NotIntersectWith(idsPlataforma);
    }

    [Fact]
    public async Task Los_filtros_de_Centro_Trabajadores_Delegacion_y_Oficio_acotan_lo_pendiente_en_la_plataforma()
    {
        var p = _plataforma.Plataforma!;
        var arnes = new Arnes(Runtime(_tenantPlataforma), new AlcanceDatosServiceFalso());

        var deSevilla = await arnes.PendientesDePlataforma.Handle(
            new ObtenerPendientesDeSubirAPlataformaEmpresaQuery(_plataforma.EmpresaId, DelegacionEmpresaId: _plataforma.SevillaId), CancellationToken.None);
        var soloAna = deSevilla.Should().ContainSingle("la Delegación deja fuera a Bea y lo de la propia Empresa").Subject;
        soloAna.TrabajadorId.Should().Be(_plataforma.AnaId);
        soloAna.TipoDocumentoId.Should().Be(p.TipoAltaId);

        var deBea = await arnes.PendientesDePlataforma.Handle(
            new ObtenerPendientesDeSubirAPlataformaEmpresaQuery(_plataforma.EmpresaId, TrabajadorIds: [_plataforma.BeaId]), CancellationToken.None);
        deBea.Select(f => f.TipoDocumentoId).Should().BeEquivalentTo([p.TipoAltaId, p.TipoPolizaId], "lo de Bea y lo de la Empresa; nada de Ana");
        deBea.Where(f => f.Ambito == AmbitoAplicacion.Trabajador).Should().OnlyContain(f => f.TrabajadorId == _plataforma.BeaId);

        var delCentro = await arnes.PendientesDePlataforma.Handle(
            new ObtenerPendientesDeSubirAPlataformaEmpresaQuery(_plataforma.EmpresaId, CentroId: _plataforma.CentroId), CancellationToken.None);
        delCentro.Should().HaveCount(3);

        var deOtroCentro = await arnes.PendientesDePlataforma.Handle(
            new ObtenerPendientesDeSubirAPlataformaEmpresaQuery(_plataforma.EmpresaId, CentroId: Guid.NewGuid()), CancellationToken.None);
        deOtroCentro.Should().BeEmpty();

        var deUnOficioSinTrabajadores = await arnes.PendientesDePlataforma.Handle(
            new ObtenerPendientesDeSubirAPlataformaEmpresaQuery(_plataforma.EmpresaId, OficioEmpresaId: Guid.NewGuid()), CancellationToken.None);
        deUnOficioSinTrabajadores.Should().BeEmpty("ningún Trabajador tiene ese Oficio y con filtro lo de la Empresa no entra");
    }

    [Fact]
    public async Task Fuera_de_la_Asignacion_de_Cartera_o_en_otro_Tenant_lo_pendiente_en_la_plataforma_sale_vacio()
    {
        // Control positivo: con alcance completo el Tenant de la plataforma ve sus tres filas, así que los vacíos de abajo no son siembra incompleta.
        (await new Arnes(Runtime(_tenantPlataforma), new AlcanceDatosServiceFalso()).PendientesDePlataforma.Handle(
            new ObtenerPendientesDeSubirAPlataformaEmpresaQuery(_plataforma.EmpresaId), CancellationToken.None)).Should().HaveCount(3);

        var fueraDeCartera = new Arnes(Runtime(_tenantPlataforma), new AlcanceDatosServiceFalso(empresaIds: [Guid.NewGuid()]));
        (await fueraDeCartera.PendientesDePlataforma.Handle(
            new ObtenerPendientesDeSubirAPlataformaEmpresaQuery(_plataforma.EmpresaId), CancellationToken.None)).Should().BeEmpty();

        var comoA = new Arnes(Runtime(_tenantA), new AlcanceDatosServiceFalso());
        (await comoA.PendientesDePlataforma.Handle(
            new ObtenerPendientesDeSubirAPlataformaEmpresaQuery(_plataforma.EmpresaId), CancellationToken.None)).Should().BeEmpty("la Empresa es de otro Tenant");
        (await comoA.PendientesDePlataforma.Handle(
            new ObtenerPendientesDeSubirAPlataformaEmpresaQuery(_a.EmpresaId), CancellationToken.None)).Should().BeEmpty("el Tenant A no tiene nada pendiente en ninguna plataforma");
    }

    // ---------- Siembra ----------

    private async Task<Escenario> SembrarAsync(Guid tenant, bool conPlanDePrevencion, bool conPlataforma = false, bool conAgendaPorTipo = false)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenant);
        var c = _propietario;
        var hoy = DiaDeNegocio.Hoy();

        if (await c.ParametrosSistema.SingleOrDefaultAsync() is null)
            c.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente Norte S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Contratista Reclamada S.L.", "B87654323");
        c.Empresas.AddRange(cliente, empresa);
        c.ContactosAgenda.Add(ContactoAgenda.DeEmpresa(empresa.Id, "Agenda Contratista", "agenda@contratista.test", esPredeterminado: true));

        var certificado = new TipoDocumento("Certificado médico", 12, true, 1, AmbitoAplicacion.Trabajador);
        var plan = new TipoDocumento("Plan de prevención", 12, true, 2, AmbitoAplicacion.Empresa);
        var formacion = new TipoDocumento("Formación de oficio", 12, true, 3, AmbitoAplicacion.Trabajador);
        c.TiposDocumento.AddRange(certificado, plan, formacion);
        await c.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Norte");
        var sevilla = new DelegacionEmpresa(empresa.Id, "Sevilla");
        sevilla.FijarContactoReclamacion("Marta Sevilla", CorreoSevilla, null);
        var malaga = new DelegacionEmpresa(empresa.Id, "Málaga");
        malaga.FijarContactoReclamacion("Luis Málaga", CorreoMalaga, null);
        c.Centros.Add(centro);
        c.DelegacionesEmpresa.AddRange(sevilla, malaga);
        await c.SaveChangesAsync();

        var ana = Trabajador.DeEmpresa(empresa.Id, "Ana", "Pérez", "00000000T");
        var bea = Trabajador.DeEmpresa(empresa.Id, "Bea", "Ruiz", "00000001R");
        if (conAgendaPorTipo)
        {
            // Sin Delegación: el lote va a la agenda de la Empresa, con un contacto predeterminado y otro dueño del Plan de prevención.
            var duenaDelPlan = ContactoAgenda.DeEmpresa(empresa.Id, "Dueña del plan", "plan@contratista.test", esPredeterminado: false);
            duenaDelPlan.EstablecerTiposDocumento([plan.Id]);
            c.ContactosAgenda.Add(duenaDelPlan);
        }
        else
        {
            ana.FijarDelegacionEmpresaYOficio(sevilla, null);
            bea.FijarDelegacionEmpresaYOficio(malaga, null);
        }
        c.Trabajadores.AddRange(ana, bea);
        await c.SaveChangesAsync();

        c.Asignaciones.AddRange(new Asignacion(ana.Id, centro.Id, hoy.AddMonths(-6)), new Asignacion(bea.Id, centro.Id, hoy.AddMonths(-6)));
        // El Centro exige el certificado y el Plan de prevención; la formación de oficio no la exige.
        c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(certificado.Id, centro.Id, incluido: true, bloqueaAcceso: true));
        c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(plan.Id, centro.Id, incluido: true, bloqueaAcceso: true));

        Documento DeTrabajador(Trabajador t, TipoDocumento tipo) =>
            Documento.DeTrabajador(t.Id, tipo.Id, hoy.AddMonths(-10), VigenciaDocumento.VenceEl(hoy.AddMonths(2)));
        var anaCertificado = DeTrabajador(ana, certificado);
        var anaFormacion = DeTrabajador(ana, formacion);
        var beaCertificado = DeTrabajador(bea, certificado);
        c.Documentos.AddRange(anaCertificado, anaFormacion, beaCertificado);
        Documento? docPlan = null;
        if (conPlanDePrevencion)
        {
            docPlan = Documento.DeEmpresa(empresa.Id, plan.Id, hoy.AddMonths(-10), VigenciaDocumento.VenceEl(hoy.AddMonths(2)));
            c.Documentos.Add(docPlan);
        }
        await c.SaveChangesAsync();

        PlataformaSembrada? plataforma = null;
        if (conPlataforma)
        {
            // Dos tipos que el Centro exige y marca como bloqueantes, con el documento vigente a 12 meses en TALVEG pero sin
            // acreditar en la plataforma del Centro: el de Ana sin subir, el de Bea subido sin validar y el de la Empresa sin subir.
            var alta = new TipoDocumento("Alta en la plataforma", null, false, 4, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
            var poliza = new TipoDocumento("Póliza de responsabilidad civil", null, false, 5, AmbitoAplicacion.Empresa, requerido: RequisitoDocumental.Si);
            var proveedor = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma del Centro Norte");
            c.TiposDocumento.AddRange(alta, poliza);
            c.ProveedoresPlataformaCae.Add(proveedor);
            await c.SaveChangesAsync();

            c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(alta.Id, centro.Id, incluido: true, bloqueaAcceso: true));
            c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(poliza.Id, centro.Id, incluido: true, bloqueaAcceso: true));
            var vencimientoLejano = VigenciaDocumento.VenceEl(hoy.AddMonths(12));
            var altaAna = Documento.DeTrabajador(ana.Id, alta.Id, hoy.AddMonths(-1), vencimientoLejano);
            var altaBea = Documento.DeTrabajador(bea.Id, alta.Id, hoy.AddMonths(-1), vencimientoLejano);
            var docPoliza = Documento.DeEmpresa(empresa.Id, poliza.Id, hoy.AddMonths(-1), vencimientoLejano);
            c.Documentos.AddRange(altaAna, altaBea, docPoliza);
            var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Plataforma del Centro", proveedor.Id, null, null, null);
            c.CanalesGestionDocumental.Add(canal);
            await c.SaveChangesAsync();

            var acreditacionBea = new AcreditacionDocumentoPlataforma(altaBea.Id, canal.Id);
            acreditacionBea.MarcarSubida();
            c.AcreditacionesDocumentoPlataforma.AddRange(
                new AcreditacionDocumentoPlataforma(altaAna.Id, canal.Id), acreditacionBea, new AcreditacionDocumentoPlataforma(docPoliza.Id, canal.Id));
            await c.SaveChangesAsync();
            plataforma = new PlataformaSembrada(alta.Id, poliza.Id, altaAna.Id, altaBea.Id, docPoliza.Id);
        }

        return new Escenario(
            empresa.Id, centro.Id, sevilla.Id, malaga.Id, ana.Id, bea.Id, certificado.Id, plan.Id,
            anaCertificado.Id, anaFormacion.Id, beaCertificado.Id, docPlan?.Id, plataforma);
    }

    private async Task<EscenarioSinBloqueo> SembrarSinBloqueoAsync(Guid tenant)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenant);
        var c = _propietario;
        var hoy = DiaDeNegocio.Hoy();

        if (await c.ParametrosSistema.SingleOrDefaultAsync() is null)
            c.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente Sur S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Contratista Sin Bloqueo S.L.", "B87654323");
        c.Empresas.AddRange(cliente, empresa);
        c.ContactosAgenda.Add(ContactoAgenda.DeEmpresa(empresa.Id, "Agenda Contratista", "agenda@contratista.test", esPredeterminado: true));
        var certificado = new TipoDocumento("Certificado médico", 12, true, 1, AmbitoAplicacion.Trabajador);
        var plan = new TipoDocumento("Plan de prevención", 12, true, 2, AmbitoAplicacion.Empresa);
        c.TiposDocumento.AddRange(certificado, plan);
        await c.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Sur");
        c.Centros.Add(centro);
        var ana = Trabajador.DeEmpresa(empresa.Id, "Ana", "Pérez", "00000000T");
        var bea = Trabajador.DeEmpresa(empresa.Id, "Bea", "Ruiz", "00000001R");
        c.Trabajadores.AddRange(ana, bea);
        await c.SaveChangesAsync();

        c.Asignaciones.AddRange(new Asignacion(ana.Id, centro.Id, hoy.AddMonths(-6)), new Asignacion(bea.Id, centro.Id, hoy.AddMonths(-6)));
        // Exigidos, ninguno bloqueante: como los Centros de la siembra del piloto.
        c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(certificado.Id, centro.Id, incluido: true));
        c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(plan.Id, centro.Id, incluido: true));
        var anaVencido = Documento.DeTrabajador(ana.Id, certificado.Id, hoy.AddMonths(-13), VigenciaDocumento.VenceEl(hoy.AddDays(-20)));
        var planVencido = Documento.DeEmpresa(empresa.Id, plan.Id, hoy.AddMonths(-13), VigenciaDocumento.VenceEl(hoy.AddDays(-5)));
        c.Documentos.AddRange(anaVencido, planVencido);
        await c.SaveChangesAsync();

        return new EscenarioSinBloqueo(empresa.Id, centro.Id, ana.Id, bea.Id, certificado.Id, anaVencido.Id, planVencido.Id);
    }

    /// <summary>Buzón genérico del Tenant y la conversación (anclada a la Empresa) que el doble devuelve como enviada: la FK es real.</summary>
    private async Task<Guid> SembrarBuzonYConversacionAsync(Guid tenant, Guid empresaId)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenant);
        _propietario.ConexionesIntegracion.Add(new ConexionIntegracion("buzon@tenant.test", "Buzón del tenant"));
        var conversacion = new Conversacion("Documentación pendiente", clienteId: null, empresaId: empresaId);
        _propietario.Conversaciones.Add(conversacion);
        await _propietario.SaveChangesAsync();
        return conversacion.Id;
    }

    // ---------- Arnés ----------

    private CaeManagerDbContext Runtime(Guid tenant)
    {
        var tenantDeLaPeticion = new TenantActualDeLaPeticion(tenant);
        var usuario = new CurrentUserServiceFalso(_usuario, tenantOrigenId: tenant);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
        _contextosRuntime.Add(contexto);
        return contexto;
    }

    /// <summary>Los handlers REALES de las dos consultas y del envío, montados sobre un mismo contexto y un mismo alcance.</summary>
    private sealed class Arnes
    {
        public MediadorDeReclamaciones Mediador { get; }
        public EmailServiceFalso Correo { get; } = new();
        public ObtenerReclamacionPorAccesoQueryHandler PorAcceso { get; }
        public ObtenerReclamacionPreventivaEmpresaQueryHandler Preventiva { get; }
        public ObtenerPendientesDeSubirAPlataformaEmpresaQueryHandler PendientesDePlataforma { get; }
        public EnviarReclamacionEmpresaEmpleadoraCommandHandler Envio { get; }

        public Arnes(CaeManagerDbContext c, IAlcanceDatosService alcance, Guid? conversacionId = null)
        {
            var agenda = new ResolucionDestinatariosAgendaService(c, c);
            Mediador = new MediadorDeReclamaciones(
                new ObtenerLoteReclamacionQueryHandler(c, c, c, c, c, c, c, c, alcance, agenda, PendientesDeReclamacionFabrica.Crear(c, alcance)),
                new ObtenerLoteReclamacionEmpresaQueryHandler(c, c, c, c, c, alcance, agenda, PendientesDeReclamacionFabrica.Crear(c, alcance)),
                new ObtenerDocumentacionBloqueantePendienteQueryHandler(c, c, c, new EvaluacionDeAccesoPorCentroService(c, c, c, c, c, alcance)),
                conversacionId ?? Guid.NewGuid());
            PendientesDePlataforma = new ObtenerPendientesDeSubirAPlataformaEmpresaQueryHandler(Mediador, alcance, c);
            var servicio = new ReclamacionEmpresaEmpleadoraService(
                c, c, new CatalogoTrabajadoresEmpresaRepository(c), alcance, PoliticaNotaInternaPruebas.Con("GestorCae"), agenda,
                new DestinoDeReclamacionesEnviadasService(c, c, c, c, c, c));
            PorAcceso = new ObtenerReclamacionPorAccesoQueryHandler(
                Mediador, alcance, c, c, c, c, new CalculoEstadoCentroService(c, c, c, c, c, c, c, c, c), servicio);
            Preventiva = new ObtenerReclamacionPreventivaEmpresaQueryHandler(Mediador, alcance, c, c, c, c, servicio);
            Envio = new EnviarReclamacionEmpresaEmpleadoraCommandHandler(
                c, c, c, c, c, c, alcance, servicio,
                new RegistroEnvioReclamacionService(
                    c, Correo, new ReclamacionDocumentalRepository(c), new CurrentUserServiceFalso(Guid.NewGuid()),
                    new CorreoDelActorRealFalso(), Mediador, NullLogger<RegistroEnvioReclamacionService>.Instance, c),
                PendientesDeReclamacionFabrica.Crear(c, alcance));
        }
    }

    private sealed class EmailServiceFalso : IEmailService
    {
        public List<string> Enviados { get; } = [];

        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo, string? responderA = null, CancellationToken cancellationToken = default)
        {
            Enviados.Add(destinatarioEmail);
            return Task.FromResult(Result.Exito());
        }
    }

    private sealed class CorreoDelActorRealFalso : ICorreoDelActorReal
    {
        public Task<string?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Reenvía las dos consultas de lote a sus handlers REALES (la cadena completa: consulta, EF y Postgres) y se queda con
    /// el único <see cref="EnviarMensajeNuevoCommand"/> que la reclamación compone. Cualquier otro request revienta a propósito.
    /// </summary>
    private sealed class MediadorDeReclamaciones(
        ObtenerLoteReclamacionQueryHandler lote, ObtenerLoteReclamacionEmpresaQueryHandler loteEmpresa,
        ObtenerDocumentacionBloqueantePendienteQueryHandler bloqueante, Guid conversacionIdADevolver) : IMediator
    {
        public List<EnviarMensajeNuevoCommand> Envios { get; } = [];
        public List<INotification> Publicados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerLoteReclamacionQuery query:
                    return (Task<TResponse>)(object)lote.Handle(query, cancellationToken);
                case ObtenerLoteReclamacionEmpresaQuery query:
                    return (Task<TResponse>)(object)loteEmpresa.Handle(query, cancellationToken);
                case ObtenerDocumentacionBloqueantePendienteQuery query:
                    return (Task<TResponse>)(object)bloqueante.Handle(query, cancellationToken);
                case EnviarMensajeNuevoCommand envio:
                    Envios.Add(envio);
                    return Task.FromResult((TResponse)(object)Result.Exito(conversacionIdADevolver));
                default:
                    throw new NotSupportedException($"El doble no cubre {request.GetType().Name}.");
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            Publicados.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
