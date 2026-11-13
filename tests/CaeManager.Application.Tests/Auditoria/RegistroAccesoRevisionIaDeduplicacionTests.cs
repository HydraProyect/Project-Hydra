using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Tests.Auditoria;

/// <summary>
/// La deduplicación del acceso «Revisión IA» (D-49, opción C) falla en cerrado hacia el registro: si la comprobación de
/// «ya registrado» no se puede hacer, se escribe la fila igualmente —la auditoría solo puede pecar por exceso— y queda un
/// aviso sin datos personales. Contra la base (filtro de Tenant, RLS, traducción a SQL) lo prueba la suite de integración
/// (<c>RegistroAccesoDocumentoSensibleServiceTests</c>); aquí, con contextos en memoria, solo la decisión del servicio.
/// </summary>
public class RegistroAccesoRevisionIaDeduplicacionTests
{
    private static readonly Guid UsuarioId = Guid.Parse("0199c0de-0000-7000-8000-0000000000b1");
    private static readonly Guid DocumentoId = Guid.Parse("0199c0de-0000-7000-8000-0000000000d1");
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

    /// <summary>Control: con la misma fila ya escrita desde que se creó la revisión, la consulta funciona y no escribe otra ni avisa.</summary>
    [Fact]
    public async Task Con_la_fila_ya_escrita_no_escribe_otra()
    {
        var repositorio = new RepositorioEspia();
        var avisos = new RegistroDeAvisos<RegistroAccesoDocumentoSensibleService>();
        var yaEscrita = new RegistroAccesoDocumentoSensible(
            DocumentoId, SensibilidadDocumental.CategoriaEspecialSalud, TipoAccesoDocumentoSensible.RevisionIa,
            UsuarioId, UsuarioId, (TipoViaAccesoAuditoria)TipoViaAcceso.Normal, null,
            (TipoActorAuditoria)ActorAuditoria.Normal(UsuarioId).ResolverTipoActor());
        var servicio = Servicio(repositorio, new AuditoriaConFilas(yaEscrita), avisos);

        await servicio.RegistrarRevisionIaSiSensibleAsync(DocumentoId, RevisionCreadaEnUtc);

        repositorio.Guardados.Should().BeEmpty();
        avisos.Entradas.Should().BeEmpty();
    }

    /// <summary>
    /// El Documento no resuelve en estos contextos vacíos, así que el servicio asume la categoría más protectora y
    /// registra: es el camino que interesa, el de una fila que sí se escribe.
    /// </summary>
    private static RegistroAccesoDocumentoSensibleService Servicio(
        RepositorioEspia repositorio, IAuditoriaQueryContext auditoria, ILogger<RegistroAccesoDocumentoSensibleService> avisos) =>
        new(new DocumentosQueryContextFalso(), new TiposDocumentoQueryContextFalso(), new ActorFijo(ActorAuditoria.Normal(UsuarioId)),
            repositorio, auditoriaContext: auditoria, logger: avisos);

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
