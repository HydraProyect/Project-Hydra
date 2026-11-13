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

    // ── La clave de deduplicación ─────────────────────────────────────────
    // Quien pide entra por una Asignación de Operación (vía OperacionDelegada con su Id), para que cada dimensión de la
    // clave —Documento, tipo, Usuario, Actor real, vía, Id de la vía, desde cuándo— se pueda variar sola. Una fila que
    // difiere en una sola de ellas no es «la misma fila»: no tapa el acceso, que se registra.

    private static readonly Guid AsignacionId = Guid.Parse("0199c0de-0000-7000-8000-0000000000a5");
    private static readonly Guid OtroId = Guid.Parse("0199c0de-0000-7000-8000-0000000000ff");
    private static readonly ActorAuditoria ActorPorOperacion = new(UsuarioId, null, TipoViaAcceso.OperacionDelegada, AsignacionId);

    private static RegistroAccesoDocumentoSensible Fila(
        Guid? documentoId = null, TipoAccesoDocumentoSensible tipo = TipoAccesoDocumentoSensible.RevisionIa,
        Guid? usuarioId = null, Guid? actorRealId = null, TipoViaAcceso via = TipoViaAcceso.OperacionDelegada,
        Guid? viaAccesoId = null, bool sinViaAccesoId = false) =>
        new(documentoId ?? DocumentoId, SensibilidadDocumental.CategoriaEspecialSalud, tipo,
            usuarioId ?? UsuarioId, actorRealId ?? UsuarioId, (TipoViaAccesoAuditoria)via,
            sinViaAccesoId ? null : viaAccesoId ?? AsignacionId,
            (TipoActorAuditoria)ActorPorOperacion.ResolverTipoActor());

    private static RegistroAccesoDocumentoSensible FilaQueDifiereEn(string dimension) => dimension switch
    {
        "Documento" => Fila(documentoId: OtroId),
        "TipoAcceso" => Fila(tipo: TipoAccesoDocumentoSensible.Apertura),
        "Usuario" => Fila(usuarioId: OtroId),
        "ActorReal" => Fila(actorRealId: OtroId),
        "Via" => Fila(via: TipoViaAcceso.Normal, sinViaAccesoId: true),
        "ViaAccesoId" => Fila(viaAccesoId: OtroId),
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, null),
    };

    /// <summary>
    /// Otro Documento de la misma persona; una Apertura previa («Descargar»); otro Usuario con el mismo Actor real
    /// (impersonación); otro Actor real con el mismo Usuario; la misma persona por la vía normal; la misma vía con otra
    /// Asignación de Operación. Ninguna es la fila de este acceso.
    /// </summary>
    [Theory]
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
    /// La composición real: <c>AddApplication</c> registra el servicio y el contenedor le inyecta el contexto de auditoría
    /// (que en producción registra Infrastructure), así que la deduplicación está activa y no apagada en silencio por un
    /// parámetro opcional que quedara a <c>null</c>.
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
        ActorAuditoria? actor = null) =>
        new(new DocumentosQueryContextFalso(), new TiposDocumentoQueryContextFalso(), new ActorFijo(actor ?? ActorAuditoria.Normal(UsuarioId)),
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
