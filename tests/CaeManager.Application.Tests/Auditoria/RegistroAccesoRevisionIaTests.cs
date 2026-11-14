using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.Seguridad.Incidentes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Application.Tests.Auditoria;

/// <summary>
/// El acceso «Revisión IA» (D-49, opción C): cada acceso del visor de Revisión IA a un Documento sensible deja una fila de
/// tipo Revisión IA, igual que cada Apertura; cuenta para la señal de extracción masiva. Sin deduplicación: dos accesos
/// seguidos dejan dos filas, también con dos revisiones pendientes del mismo Documento, y una fila igual ya escrita no tapa
/// la siguiente. Contra la base (función de registro, RLS) lo prueba la suite de integración
/// (<c>RegistroAccesoDocumentoSensibleServiceTests</c>); aquí, con contextos en memoria, solo la decisión del servicio.
/// </summary>
public class RegistroAccesoRevisionIaTests
{
    private static readonly Guid UsuarioId = Guid.Parse("0199c0de-0000-7000-8000-0000000000b1");
    private static readonly Guid DocumentoId = Guid.Parse("0199c0de-0000-7000-8000-0000000000d1");
    private static readonly Guid TenantPropietarioId = Guid.Parse("0199c0de-0000-7000-8000-0000000000e1");

    /// <summary>
    /// Revisión IA y Apertura, por el mismo camino: dos accesos seguidos dejan dos filas del tipo pedido y dos avisos a la
    /// señal de extracción masiva. Si un <c>return</c> temprano volviera a saltarse la fila del segundo acceso, cae.
    /// </summary>
    [Theory]
    [InlineData(TipoAccesoDocumentoSensible.RevisionIa)]
    [InlineData(TipoAccesoDocumentoSensible.Apertura)]
    public async Task Dos_accesos_seguidos_dejan_dos_filas_y_dos_avisos_a_la_senal(TipoAccesoDocumentoSensible tipo)
    {
        var repositorio = new RepositorioEspia();
        var senales = new SenalesEspia();
        var servicio = Servicio(new DocumentosQueryContextFalso(), repositorio, senales);

        await servicio.RegistrarSiSensibleAsync(DocumentoId, tipo);
        await servicio.RegistrarSiSensibleAsync(DocumentoId, tipo);

        repositorio.Guardados.Should().HaveCount(2, "cada acceso deja su fila")
            .And.OnlyContain(r => r.TipoAcceso == tipo && r.DocumentoId == DocumentoId && r.UsuarioId == UsuarioId);
        senales.Avisos.Should().Equal([(UsuarioId, (Guid?)TenantPropietarioId), (UsuarioId, (Guid?)TenantPropietarioId)],
            "cada acceso cuenta para la señal de extracción masiva");
    }

    /// <summary>
    /// Dos revisiones pendientes del mismo Documento, abiertas una tras otra y la primera otra vez: tres accesos, tres filas
    /// Revisión IA y tres avisos.
    /// </summary>
    [Fact]
    public async Task Con_dos_revisiones_pendientes_del_mismo_Documento_cada_acceso_deja_su_fila()
    {
        var documento = Documento.DeEmpresa(Guid.NewGuid(), Guid.NewGuid(), DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca, "archivo.pdf");
        var documentos = new DocumentosQueryContextFalso();
        documentos.ListaDocumentos.Add(documento);
        documentos.ListaRevisionesIaDocumento.AddRange([
            RevisionIaDocumento.Crear(documento.Id, 50, null, null, null, null, "Confianza baja (50%)"),
            RevisionIaDocumento.Crear(documento.Id, 40, null, null, null, null, "Confianza baja (40%)")]);
        var repositorio = new RepositorioEspia();
        var senales = new SenalesEspia();
        var servicio = Servicio(documentos, repositorio, senales);

        for (var i = 0; i < 3; i++)
            await servicio.RegistrarSiSensibleAsync(documento.Id, TipoAccesoDocumentoSensible.RevisionIa);

        repositorio.Guardados.Should().HaveCount(3).And.OnlyContain(r => r.TipoAcceso == TipoAccesoDocumentoSensible.RevisionIa);
        senales.Avisos.Should().HaveCount(3);
    }

    /// <summary>
    /// Falla en cerrado igual que los demás tipos: si el repositorio no deja la fila, el acceso no se sirve
    /// (<see cref="AccesoDocumentoSensibleNoRegistradoException"/>) y no se avisa a la señal.
    /// </summary>
    [Fact]
    public async Task Si_la_fila_Revision_IA_no_se_guarda_falla_en_cerrado_sin_avisar_a_la_senal()
    {
        var senales = new SenalesEspia();
        var servicio = Servicio(new DocumentosQueryContextFalso(), new RepositorioEspia { Rechaza = true }, senales);

        var acto = () => servicio.RegistrarSiSensibleAsync(DocumentoId, TipoAccesoDocumentoSensible.RevisionIa);

        await acto.Should().ThrowAsync<AccesoDocumentoSensibleNoRegistradoException>();
        senales.Avisos.Should().BeEmpty();
    }

    /// <summary>
    /// La composición real: <c>AddApplication</c> registra el servicio, y aunque el contenedor tenga un contexto de
    /// auditoría con la fila Revisión IA idéntica ya escrita, cada acceso deja la suya: nada la consulta para saltársela.
    /// </summary>
    [Fact]
    public async Task Compuesto_por_AddApplication_una_fila_identica_ya_escrita_no_tapa_el_acceso()
    {
        var repositorio = new RepositorioEspia();
        var yaEscrita = new RegistroAccesoDocumentoSensible(
            DocumentoId, SensibilidadDocumental.CategoriaEspecialSalud, TipoAccesoDocumentoSensible.RevisionIa,
            UsuarioId, UsuarioId, (TipoViaAccesoAuditoria)TipoViaAcceso.Normal, null,
            (TipoActorAuditoria)ActorAuditoria.Normal(UsuarioId).ResolverTipoActor());
        var servicios = new ServiceCollection();
        CaeManager.Application.DependencyInjection.ApplicationServiceCollectionExtensions.AddApplication(servicios);
        servicios.AddScoped<CaeManager.Application.Documentos.IDocumentosQueryContext>(_ => new DocumentosQueryContextFalso());
        servicios.AddScoped<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(_ => new TiposDocumentoQueryContextFalso());
        servicios.AddScoped<IActorAuditoria>(_ => new ActorFijo(ActorAuditoria.Normal(UsuarioId)));
        servicios.AddScoped<IRegistroAccesoDocumentoSensibleRepository>(_ => repositorio);
        servicios.AddScoped<IAuditoriaQueryContext>(_ => new AuditoriaConFilas(yaEscrita));
        servicios.AddScoped<ITenantActual>(_ => new TenantFijo(TenantPropietarioId));
        using var proveedor = servicios.BuildServiceProvider();
        using var ambito = proveedor.CreateScope();
        var servicio = ambito.ServiceProvider.GetRequiredService<IRegistroAccesoDocumentoSensibleService>();

        await servicio.RegistrarSiSensibleAsync(DocumentoId, TipoAccesoDocumentoSensible.RevisionIa);
        await servicio.RegistrarSiSensibleAsync(DocumentoId, TipoAccesoDocumentoSensible.RevisionIa);

        repositorio.Guardados.Should().HaveCount(2);
    }

    /// <summary>
    /// El Documento no resuelve en un contexto vacío, así que el servicio asume la categoría más protectora y registra: es
    /// el camino que interesa, el de una fila que sí se escribe.
    /// </summary>
    private static RegistroAccesoDocumentoSensibleService Servicio(
        DocumentosQueryContextFalso documentos, RepositorioEspia repositorio, ISenalesIncidenteSeguridad senales) =>
        new(documentos, new TiposDocumentoQueryContextFalso(), new ActorFijo(ActorAuditoria.Normal(UsuarioId)), repositorio,
            senalesIncidente: senales, tenantActual: new TenantFijo(TenantPropietarioId));

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

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class RepositorioEspia : IRegistroAccesoDocumentoSensibleRepository
    {
        public List<RegistroAccesoDocumentoSensible> Guardados { get; } = [];
        public bool Rechaza { get; init; }

        public Task<bool> GuardarAsync(RegistroAccesoDocumentoSensible registro, CancellationToken cancellationToken = default)
        {
            if (Rechaza)
                return Task.FromResult(false);
            Guardados.Add(registro);
            return Task.FromResult(true);
        }
    }

    private sealed class AuditoriaConFilas(params RegistroAccesoDocumentoSensible[] filas) : IAuditoriaQueryContext
    {
        public IQueryable<RegistroAuditoria> RegistrosAuditoria =>
            new TestAsyncQueryable<RegistroAuditoria>(Array.Empty<RegistroAuditoria>().AsQueryable());
        public IQueryable<RegistroAccesoDocumentoSensible> RegistrosAccesoDocumentoSensible =>
            new TestAsyncQueryable<RegistroAccesoDocumentoSensible>(filas.AsQueryable());
    }
}
