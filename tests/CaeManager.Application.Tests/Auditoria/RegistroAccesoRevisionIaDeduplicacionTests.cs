using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Seguridad.Incidentes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Tests.Auditoria;

/// <summary>
/// El acceso «Revisión IA» (D-49, opción C) separa la fila de auditoría de la señal de extracción masiva: la fila se
/// deduplica —como mucho una por persona y Documento desde que se creó la revisión pendiente; peticiones simultáneas pueden
/// dejar más, nunca menos—, y cada acceso cuenta para la señal, se escriba la fila o no. La deduplicación falla en cerrado
/// hacia el registro: si la comprobación de «ya registrado» no se puede hacer, se escribe la fila igualmente y queda un aviso
/// sin datos personales. Contra la base (filtro de Tenant, RLS, traducción a SQL) lo prueba la suite de integración
/// (<c>RegistroAccesoDocumentoSensibleServiceTests</c>); aquí, con contextos en memoria, solo la decisión del servicio.
/// </summary>
public class RegistroAccesoRevisionIaDeduplicacionTests
{
    private static readonly Guid UsuarioId = Guid.Parse("0199c0de-0000-7000-8000-0000000000b1");
    private static readonly Guid DocumentoId = Guid.Parse("0199c0de-0000-7000-8000-0000000000d1");
    private static readonly Guid TenantPropietarioId = Guid.Parse("0199c0de-0000-7000-8000-0000000000e1");
    private static readonly DateTime RevisionCreadaEnUtc = DateTime.UtcNow.AddHours(-1);

    [Fact]
    public async Task Si_la_consulta_de_ya_registrado_falla_escribe_la_fila_y_avisa_sin_datos_personales()
    {
        var repositorio = new RepositorioEspia();
        var avisos = new RegistroDeAvisos<RegistroAccesoDocumentoSensibleService>();
        var servicio = Servicio(repositorio, new AuditoriaQueCae(), avisos);

        await servicio.RegistrarRevisionIaSiSensibleAsync(DocumentoId, RevisionCreadaEnUtc);

        repositorio.Guardados.Should().ContainSingle("sin poder comprobar, se registra: de más, nunca de menos")
            .Which.TipoAcceso.Should().Be(TipoAccesoDocumentoSensible.RevisionIa);
        avisos.Entradas.Should().ContainSingle().Which.Nivel.Should().Be(LogLevel.Warning);
        var texto = avisos.Entradas[0].Texto;
        texto.Should().Contain(nameof(InvalidOperationException), "basta el tipo de la excepción");
        texto.Should().NotContain(DocumentoId.ToString()).And.NotContain(UsuarioId.ToString())
            .And.NotContain(AuditoriaQueCae.Mensaje, "ni el mensaje de la excepción, que podría llevar datos");
    }

    /// <summary>
    /// Sin Tenant actual no hay con qué acotar la comprobación: no deduplica y escribe la fila, aunque haya una idéntica.
    /// </summary>
    [Fact]
    public async Task Sin_Tenant_actual_no_deduplica_y_escribe_la_fila()
    {
        var repositorio = new RepositorioEspia();
        var servicio = Servicio(repositorio, new AuditoriaConFilas(Fila()), actor: ActorPorOperacion, sinTenant: true);

        await servicio.RegistrarRevisionIaSiSensibleAsync(DocumentoId, RevisionCreadaEnUtc);

        repositorio.Guardados.Should().ContainSingle("sin Tenant actual, se registra: de más, nunca de menos");
    }

    /// <summary>
    /// Sin contexto de auditoría no hay con qué comprobar: registra en cada llamada, de más, nunca de menos, y no lo trata
    /// como un fallo de la comprobación (sin aviso): es la composición sin deduplicación, no una consulta caída.
    /// </summary>
    [Fact]
    public async Task Sin_contexto_de_auditoria_no_deduplica_y_escribe_la_fila()
    {
        var repositorio = new RepositorioEspia();
        var avisos = new RegistroDeAvisos<RegistroAccesoDocumentoSensibleService>();
        var servicio = new RegistroAccesoDocumentoSensibleService(
            new DocumentosQueryContextFalso(), new TiposDocumentoQueryContextFalso(), new ActorFijo(ActorPorOperacion),
            repositorio, tenantActual: new TenantFijo(TenantPropietarioId), auditoriaContext: null, logger: avisos);

        await servicio.RegistrarRevisionIaSiSensibleAsync(DocumentoId, RevisionCreadaEnUtc);
        await servicio.RegistrarRevisionIaSiSensibleAsync(DocumentoId, RevisionCreadaEnUtc);

        repositorio.Guardados.Should().HaveCount(2);
        avisos.Entradas.Should().BeEmpty("sin contexto no hay consulta que falle");
    }

    /// <summary>
    /// Sin Actor real no hay de quién deduplicar: una fila idéntica, también sin Actor real, no tapa el acceso.
    /// </summary>
    [Fact]
    public async Task Sin_Actor_real_no_deduplica_aunque_haya_una_fila_identica()
    {
        var repositorio = new RepositorioEspia();
        var sinActorReal = new ActorAuditoria(null, null, TipoViaAcceso.Normal, null);
        var identica = ConTenant(new RegistroAccesoDocumentoSensible(
            DocumentoId, SensibilidadDocumental.CategoriaEspecialSalud, TipoAccesoDocumentoSensible.RevisionIa,
            null, null, (TipoViaAccesoAuditoria)TipoViaAcceso.Normal, null,
            (TipoActorAuditoria)sinActorReal.ResolverTipoActor()), TenantPropietarioId);
        var servicio = Servicio(repositorio, new AuditoriaConFilas(identica), actor: sinActorReal);

        await servicio.RegistrarRevisionIaSiSensibleAsync(DocumentoId, RevisionCreadaEnUtc);

        repositorio.Guardados.Should().ContainSingle("sin Actor real, se registra: de más, nunca de menos");
    }

    /// <summary>
    /// La fila ya escrita desde que se creó la revisión tapa la fila de este acceso, pero no la señal: se avisa igual que
    /// si se hubiera escrito, sin aviso de fallo.
    /// </summary>
    [Fact]
    public async Task Con_la_fila_ya_escrita_no_escribe_otra_pero_avisa_a_la_senal()
    {
        var repositorio = new RepositorioEspia();
        var avisos = new RegistroDeAvisos<RegistroAccesoDocumentoSensibleService>();
        var senales = new SenalesEspia();
        var yaEscrita = ConTenant(new RegistroAccesoDocumentoSensible(
            DocumentoId, SensibilidadDocumental.CategoriaEspecialSalud, TipoAccesoDocumentoSensible.RevisionIa,
            UsuarioId, UsuarioId, (TipoViaAccesoAuditoria)TipoViaAcceso.Normal, null,
            (TipoActorAuditoria)ActorAuditoria.Normal(UsuarioId).ResolverTipoActor()), TenantPropietarioId);
        var servicio = Servicio(repositorio, new AuditoriaConFilas(yaEscrita), avisos, senales: senales);

        await servicio.RegistrarRevisionIaSiSensibleAsync(DocumentoId, RevisionCreadaEnUtc);

        repositorio.Guardados.Should().BeEmpty();
        avisos.Entradas.Should().BeEmpty();
        senales.Avisos.Should().ContainSingle("cada acceso cuenta para la señal, se escriba la fila o no")
            .Which.Should().Be((UsuarioId, (Guid?)TenantPropietarioId));
    }

    // ── La clave de deduplicación ─────────────────────────────────────────
    // Quien pide entra por una Asignación de Operación (vía OperacionDelegada con su Id), para que cada dimensión de la
    // clave —Tenant, Documento, tipo, Usuario, Actor real, vía, Id de la vía, desde cuándo— se pueda variar sola. Una fila
    // que difiere en una sola de ellas no es «la misma fila»: no tapa el acceso, que se registra.

    private static readonly Guid AsignacionId = Guid.Parse("0199c0de-0000-7000-8000-0000000000a5");
    private static readonly Guid OtroId = Guid.Parse("0199c0de-0000-7000-8000-0000000000ff");
    private static readonly ActorAuditoria ActorPorOperacion = new(UsuarioId, null, TipoViaAcceso.OperacionDelegada, AsignacionId);

    private static RegistroAccesoDocumentoSensible Fila(
        Guid? documentoId = null, TipoAccesoDocumentoSensible tipo = TipoAccesoDocumentoSensible.RevisionIa,
        Guid? usuarioId = null, Guid? actorRealId = null, TipoViaAcceso via = TipoViaAcceso.OperacionDelegada,
        Guid? viaAccesoId = null, bool sinViaAccesoId = false, Guid? tenantId = null) =>
        ConTenant(new(documentoId ?? DocumentoId, SensibilidadDocumental.CategoriaEspecialSalud, tipo,
            usuarioId ?? UsuarioId, actorRealId ?? UsuarioId, (TipoViaAccesoAuditoria)via,
            sinViaAccesoId ? null : viaAccesoId ?? AsignacionId,
            (TipoActorAuditoria)ActorPorOperacion.ResolverTipoActor()), tenantId ?? TenantPropietarioId);

    /// <summary>El Tenant de la fila lo sella Infrastructure al guardar; aquí se fija a mano.</summary>
    private static RegistroAccesoDocumentoSensible ConTenant(RegistroAccesoDocumentoSensible fila, Guid tenantId)
    {
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(fila, tenantId);
        return fila;
    }

    private static RegistroAccesoDocumentoSensible FilaQueDifiereEn(string dimension) => dimension switch
    {
        // Otro Tenant propietario: la RLS y el filtro global la esconderían; la comparación explícita, también.
        "Tenant" => Fila(tenantId: OtroId),
        "Documento" => Fila(documentoId: OtroId),
        "TipoAcceso" => Fila(tipo: TipoAccesoDocumentoSensible.Apertura),
        "Usuario" => Fila(usuarioId: OtroId),
        "ActorReal" => Fila(actorRealId: OtroId),
        // El mismo Id de vía con otro tipo de vía: aísla la columna ViaAcceso (con la vía normal cambiarían las dos).
        "Via" => Fila(via: TipoViaAcceso.SesionPrivilegiada),
        "ViaAccesoId" => Fila(viaAccesoId: OtroId),
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, null),
    };

    /// <summary>
    /// Otro Tenant propietario; otro Documento de la misma persona; una Apertura previa («Descargar»); otro Usuario con el
    /// mismo Actor real (impersonación); otro Actor real con el mismo Usuario; el mismo Id de vía con otro tipo de vía; la
    /// misma vía con otra Asignación de Operación. Ninguna es la fila de este acceso.
    /// </summary>
    [Theory]
    [InlineData("Tenant")]
    [InlineData("Documento")]
    [InlineData("TipoAcceso")]
    [InlineData("Usuario")]
    [InlineData("ActorReal")]
    [InlineData("Via")]
    [InlineData("ViaAccesoId")]
    public async Task Una_fila_que_difiere_en_una_dimension_de_la_clave_no_tapa_el_acceso(string dimension)
    {
        var previa = FilaQueDifiereEn(dimension);
        var caso = $"la fila previa difiere en {dimension}";
        var repositorio = new RepositorioEspia();
        var servicio = Servicio(repositorio, new AuditoriaConFilas(previa), actor: ActorPorOperacion);

        await servicio.RegistrarRevisionIaSiSensibleAsync(DocumentoId, RevisionCreadaEnUtc);

        repositorio.Guardados.Should().ContainSingle(caso);
    }

    /// <summary>Control de la teoría: la fila idéntica, por la misma Asignación de Operación, sí cuenta como ya registrada.</summary>
    [Fact]
    public async Task La_fila_identica_por_la_misma_via_cuenta_como_ya_registrada()
    {
        var repositorio = new RepositorioEspia();
        var servicio = Servicio(repositorio, new AuditoriaConFilas(Fila()), actor: ActorPorOperacion);

        await servicio.RegistrarRevisionIaSiSensibleAsync(DocumentoId, RevisionCreadaEnUtc);

        repositorio.Guardados.Should().BeEmpty();
    }

    /// <summary>Una fila anterior a la revisión (de otra revisión ya resuelta del mismo Documento) no cuenta para esta.</summary>
    [Fact]
    public async Task Una_fila_anterior_a_la_creacion_de_la_revision_no_la_tapa()
    {
        var repositorio = new RepositorioEspia();
        var servicio = Servicio(repositorio, new AuditoriaConFilas(Fila()), actor: ActorPorOperacion);

        await servicio.RegistrarRevisionIaSiSensibleAsync(DocumentoId, DateTime.UtcNow.AddHours(1));

        repositorio.Guardados.Should().ContainSingle();
    }

    /// <summary>
    /// Sin cota por la ventana de la señal: una fila de la misma revisión pendiente escrita hace días sigue tapando la
    /// fila de hoy (la señal ya no depende de ella), y el acceso cuenta igualmente para la señal.
    /// </summary>
    [Fact]
    public async Task Una_fila_de_la_misma_revision_de_hace_dias_sigue_deduplicando_y_avisa_a_la_senal()
    {
        var repositorio = new RepositorioEspia();
        var senales = new SenalesEspia();
        var deHaceDias = Fila();
        typeof(RegistroAccesoDocumentoSensible).GetProperty(nameof(RegistroAccesoDocumentoSensible.OcurridoEnUtc))!
            .SetValue(deHaceDias, DateTime.UtcNow.AddDays(-2));
        var servicio = Servicio(repositorio, new AuditoriaConFilas(deHaceDias), actor: ActorPorOperacion, senales: senales);

        await servicio.RegistrarRevisionIaSiSensibleAsync(DocumentoId, DateTime.UtcNow.AddDays(-3));

        repositorio.Guardados.Should().BeEmpty("como mucho una fila por persona y Documento desde que se creó la revisión");
        senales.Avisos.Should().ContainSingle();
    }

    /// <summary>
    /// La señal real tras un «reinicio»: una instancia nueva de la señal, con su contador vacío, y la fila de la revisión
    /// ya persistida. N relecturas del mismo Documento por Revisión IA, todas deduplicadas, suman N y disparan la extracción
    /// masiva igual que N Aperturas.
    /// </summary>
    [Fact]
    public async Task Tras_un_reinicio_N_relecturas_deduplicadas_suman_N_igual_que_N_aperturas()
    {
        const int umbral = 3;
        var opciones = new OpcionesIncidentesSeguridad { UmbralDocumentosSensibles = umbral };

        var incidentesPorRevisionIa = new RegistroIncidentesEspia();
        var repositorioRevisionIa = new RepositorioEspia();
        var revisionIa = Servicio(repositorioRevisionIa, new AuditoriaConFilas(Fila()), actor: ActorPorOperacion,
            senales: SenalReal(incidentesPorRevisionIa, opciones));
        for (var i = 0; i < umbral; i++)
            await revisionIa.RegistrarRevisionIaSiSensibleAsync(DocumentoId, RevisionCreadaEnUtc);

        var incidentesPorApertura = new RegistroIncidentesEspia();
        var apertura = Servicio(new RepositorioEspia(), new AuditoriaConFilas(), actor: ActorPorOperacion,
            senales: SenalReal(incidentesPorApertura, opciones));
        for (var i = 0; i < umbral; i++)
            await apertura.RegistrarSiSensibleAsync(DocumentoId, TipoAccesoDocumentoSensible.Apertura);

        repositorioRevisionIa.Guardados.Should().BeEmpty("control: las N relecturas quedan tapadas por la fila persistida");
        incidentesPorApertura.Senales.Should().ContainSingle("control: N aperturas alcanzan el umbral")
            .Which.Should().Be(CaeManager.Domain.Plataforma.SenalIncidenteSeguridad.ExtraccionMasiva);
        incidentesPorRevisionIa.Senales.Should().Equal(incidentesPorApertura.Senales,
            "deduplicar la fila no puede esconder la relectura a una señal que se vació al reiniciar");
    }

    /// <summary>
    /// La composición real: <c>AddApplication</c> registra el servicio y el contenedor le inyecta el contexto de auditoría
    /// y el Tenant actual (que en producción registran Infrastructure y la Web), así que la deduplicación está activa y no
    /// apagada en silencio por un parámetro opcional que quedara a <c>null</c>.
    /// </summary>
    [Fact]
    public async Task Compuesto_por_AddApplication_deduplica_con_el_contexto_de_auditoria_inyectado()
    {
        var repositorio = new RepositorioEspia();
        var servicios = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        CaeManager.Application.DependencyInjection.ApplicationServiceCollectionExtensions.AddApplication(servicios);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<CaeManager.Application.Documentos.IDocumentosQueryContext>(
            servicios, _ => new DocumentosQueryContextFalso());
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(
            servicios, _ => new TiposDocumentoQueryContextFalso());
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IActorAuditoria>(
            servicios, _ => new ActorFijo(ActorAuditoria.Normal(UsuarioId)));
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IRegistroAccesoDocumentoSensibleRepository>(
            servicios, _ => repositorio);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IAuditoriaQueryContext>(
            servicios, _ => new AuditoriaConFilas(Fila(via: TipoViaAcceso.Normal, sinViaAccesoId: true)));
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<ITenantActual>(
            servicios, _ => new TenantFijo(TenantPropietarioId));
        using var proveedor = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(servicios);
        using var ambito = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(proveedor);
        var servicio = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<IRegistroAccesoDocumentoSensibleService>(ambito.ServiceProvider);

        await servicio.RegistrarRevisionIaSiSensibleAsync(DocumentoId, RevisionCreadaEnUtc);

        repositorio.Guardados.Should().BeEmpty("la fila ya consta: el contenedor inyectó el contexto de auditoría");
    }

    /// <summary>
    /// El Documento no resuelve en estos contextos vacíos, así que el servicio asume la categoría más protectora y
    /// registra: es el camino que interesa, el de una fila que sí se escribe.
    /// </summary>
    private static RegistroAccesoDocumentoSensibleService Servicio(
        RepositorioEspia repositorio, IAuditoriaQueryContext auditoria, ILogger<RegistroAccesoDocumentoSensibleService>? avisos = null,
        ActorAuditoria? actor = null, ISenalesIncidenteSeguridad? senales = null, bool sinTenant = false) =>
        new(new DocumentosQueryContextFalso(), new TiposDocumentoQueryContextFalso(), new ActorFijo(actor ?? ActorAuditoria.Normal(UsuarioId)),
            repositorio, senalesIncidente: senales, tenantActual: sinTenant ? null : new TenantFijo(TenantPropietarioId),
            auditoriaContext: auditoria, logger: avisos);

    private static SenalesIncidenteSeguridad SenalReal(RegistroIncidentesEspia registro, OpcionesIncidentesSeguridad opciones) =>
        new(registro, new AlertaQueNoHaceNada(), Options.Create(opciones), TimeProvider.System, NullLogger<SenalesIncidenteSeguridad>.Instance);

    private sealed class TenantFijo(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class SenalesEspia : ISenalesIncidenteSeguridad
    {
        public List<(Guid ActorReal, Guid? Tenant)> Avisos { get; } = [];

        public Task DocumentoSensibleAbiertoAsync(Guid actorRealUsuarioId, Guid? tenantId, CancellationToken ct = default)
        {
            Avisos.Add((actorRealUsuarioId, tenantId));
            return Task.CompletedTask;
        }

        public Task InicioSesionNoReconocidoAsync(Guid usuarioId, Guid tenantPropietarioId, bool teniaContrasena, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task ExportacionAsync(Guid actorRealUsuarioId, Guid? tenantId, int filas, CancellationToken ct = default) => Task.CompletedTask;
        public Task AccesoAOtroTenantRechazadoAsync(Guid usuarioId, Guid tenantOrigenId, Guid tenantObjetivoId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RegistroIncidentesEspia : IRegistroIncidentesSeguridad
    {
        public List<CaeManager.Domain.Plataforma.SenalIncidenteSeguridad> Senales { get; } = [];

        public Task<IncidenteRegistrado> RegistrarAsync(
            CaeManager.Domain.Plataforma.SenalIncidenteSeguridad senal, string sujetoClave, IReadOnlyCollection<Guid> tenantsAfectados,
            EvidenciaIncidenteSeguridad evidencia, TimeSpan ventanaDeduplicacion, CancellationToken ct = default)
        {
            Senales.Add(senal);
            return Task.FromResult(new IncidenteRegistrado(Guid.NewGuid(), true));
        }
    }

    private sealed class AlertaQueNoHaceNada : IAlertaIncidenteSeguridad
    {
        public void Encolar(Guid incidenteId) { }
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class RepositorioEspia : IRegistroAccesoDocumentoSensibleRepository
    {
        public List<RegistroAccesoDocumentoSensible> Guardados { get; } = [];

        public Task<bool> GuardarAsync(RegistroAccesoDocumentoSensible registro, CancellationToken cancellationToken = default)
        {
            Guardados.Add(registro);
            return Task.FromResult(true);
        }
    }

    /// <summary>La lectura del rastro falla (conexión caída, RLS que rechaza la consulta).</summary>
    private sealed class AuditoriaQueCae : IAuditoriaQueryContext
    {
        internal const string Mensaje = "consulta caída con datos";

        public IQueryable<RegistroAuditoria> RegistrosAuditoria => throw new InvalidOperationException(Mensaje);
        public IQueryable<RegistroAccesoDocumentoSensible> RegistrosAccesoDocumentoSensible => throw new InvalidOperationException(Mensaje);
    }

    private sealed class AuditoriaConFilas(params RegistroAccesoDocumentoSensible[] filas) : IAuditoriaQueryContext
    {
        public IQueryable<RegistroAuditoria> RegistrosAuditoria =>
            new TestAsyncQueryable<RegistroAuditoria>(Array.Empty<RegistroAuditoria>().AsQueryable());
        public IQueryable<RegistroAccesoDocumentoSensible> RegistrosAccesoDocumentoSensible =>
            new TestAsyncQueryable<RegistroAccesoDocumentoSensible>(filas.AsQueryable());
    }
}
