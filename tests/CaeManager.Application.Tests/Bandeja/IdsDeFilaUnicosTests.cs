using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Bandeja;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPendientes;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Application.Tests.Bandeja;

/// <summary>
/// Red de unicidad del <see cref="ItemBandejaDto.Id"/> (continuación de #1064). El Id es el <c>@key</c> del <c>@foreach</c> que pinta
/// la fila: dos filas hermanas con el mismo Id matan el circuito de Blazor. Tres propiedades, cada una en su capa:
///
/// <list type="number">
/// <item><b>Cada constructor lleva todas las dimensiones de su fila</b> (<see cref="IdDeFilaDeCola"/>): variar una sola dimensión
/// cambia el Id, y dos tipos de fila nunca comparten Id aunque compartan Guid.</item>
/// <item><b>Datos que generan hermanos no dan Ids repetidos</b> a través de la fusión real: el mismo Trabajador y Tipo en varios
/// Centros, una Empresa bloqueante × varios Trabajadores × varios Centros (R2 de #1069), una Asignación repetida.</item>
/// <item><b>Si un productor aun así repite un Id, Mi trabajo lo dice</b> (log de error por Tenant): la pantalla numera la repetición
/// y no muere, pero el defecto del productor no se queda mudo.</item>
/// </list>
///
/// <para>
/// Contrato efectivo: el Id es único dentro de la cola de UN Tenant; el mismo trío Trabajador-Centro-Tipo en dos Tenants da el mismo
/// Id (no lleva Tenant: <see cref="ItemBandejaDto"/> no lo tiene), y la vista de Mi trabajo, que mezcla Tenants en un grupo por
/// severidad, lo añade a la identidad de su clave (<c>ClavesUnicasEnListasTests</c> y <c>MiTrabajoClavesUnicasTests</c>, en Web).
/// </para>
/// </summary>
public class IdsDeFilaUnicosTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 3);

    // ------------------------------------------------ 1. el constructor lleva todas sus dimensiones

    [Fact]
    public void El_Id_de_una_alerta_sin_documento_cambia_con_cada_una_de_sus_tres_dimensiones()
    {
        var (trabajador, tipo, centro) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var baseId = IdDeFilaDeCola.Alerta("alerta", null, trabajador, tipo, centro);

        IdDeFilaDeCola.Alerta("alerta", null, Guid.NewGuid(), tipo, centro).Should().NotBe(baseId, "Trabajador");
        IdDeFilaDeCola.Alerta("alerta", null, trabajador, Guid.NewGuid(), centro).Should().NotBe(baseId, "Tipo de documento");
        IdDeFilaDeCola.Alerta("alerta", null, trabajador, tipo, Guid.NewGuid()).Should().NotBe(baseId, "Centro");
        IdDeFilaDeCola.Alerta("alerta", null, trabajador, tipo, null).Should().NotBe(baseId, "sin Centro no es un Centro cualquiera");
        IdDeFilaDeCola.Alerta("proximo", null, trabajador, tipo, centro).Should().NotBe(baseId, "el prefijo separa los tipos de fila");
    }

    [Fact]
    public void El_Id_de_una_alerta_con_documento_es_el_documento_y_no_depende_del_resto()
    {
        var documento = Guid.NewGuid();

        IdDeFilaDeCola.Alerta("alerta", documento, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
            .Should().Be(IdDeFilaDeCola.Alerta("alerta", documento, Guid.NewGuid(), Guid.NewGuid(), null));
        IdDeFilaDeCola.Alerta("alerta", documento, Guid.NewGuid(), Guid.NewGuid(), null)
            .Should().NotBe(IdDeFilaDeCola.Alerta("alerta", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null));
    }

    [Fact]
    public void El_Id_de_un_requisito_cambia_con_el_Centro_el_Trabajador_y_el_Tipo()
    {
        var (centro, trabajador, tipo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var baseId = IdDeFilaDeCola.Requisito(centro, trabajador, tipo);

        IdDeFilaDeCola.Requisito(Guid.NewGuid(), trabajador, tipo).Should().NotBe(baseId, "Centro");
        IdDeFilaDeCola.Requisito(centro, Guid.NewGuid(), tipo).Should().NotBe(baseId, "Trabajador");
        IdDeFilaDeCola.Requisito(centro, trabajador, Guid.NewGuid()).Should().NotBe(baseId, "Tipo de documento");
    }

    [Fact]
    public void Dos_tipos_de_fila_nunca_comparten_Id_aunque_compartan_Guid()
    {
        var g = Guid.NewGuid();
        string[] ids =
        [
            IdDeFilaDeCola.Alerta("alerta", g, g, g, g), IdDeFilaDeCola.Alerta("proximo", g, g, g, g), IdDeFilaDeCola.Revision(g),
            IdDeFilaDeCola.Visita(g), IdDeFilaDeCola.SugerenciaVisita(g), IdDeFilaDeCola.Deteccion(g), IdDeFilaDeCola.Plataforma(g),
            IdDeFilaDeCola.PlataformaVencida(g), IdDeFilaDeCola.Seguimiento(g), IdDeFilaDeCola.Requisito(g, g, g),
        ];

        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Duplicados_cuenta_los_Ids_repetidos_y_no_marca_los_unicos()
    {
        ItemBandejaDto Item(string id) => new(id, TipoItemBandeja.Faltante, "t", "s", null, null, null, null, null, null);

        IdDeFilaDeCola.Duplicados([Item("a"), Item("b"), Item("a"), Item("a")], i => i.Id).Should().Equal(new Dictionary<string, int> { ["a"] = 3 });
        IdDeFilaDeCola.Duplicados([Item("a"), Item("b")], i => i.Id).Should().BeEmpty();
        IdDeFilaDeCola.Duplicados(Array.Empty<ItemBandejaDto>(), i => i.Id).Should().BeEmpty();
    }

    // ------------------------------------- 2. datos que generan hermanos, a través de la fusión real

    private static AlertaDto Faltante(Guid trabajador, Guid tipo, Guid? centro) => new(
        DocumentoId: null, TrabajadorId: trabajador, TrabajadorNombre: "Ana García", TipoDocumentoId: tipo,
        TipoDocumentoNombre: "Apto médico", FechaVencimiento: null, Estado: EstadoDocumento.Faltante, ArchivoUrl: null,
        CentroNombre: centro is null ? null : "Centro", CentroId: centro);

    private static DocumentacionBloqueantePendienteDto Pendiente(BloqueoDeAccesoDeTrabajador b) => new(
        CentroId: b.CentroId, CentroNombre: "Centro", TrabajadorId: b.TrabajadorId, TrabajadorNombre: "Ana García",
        TipoDocumentoId: b.TipoDocumentoId, TipoDocumentoNombre: "Tipo", EmpresaId: b.EmpresaId,
        Ambito: b.Ambito, Situacion: b.Situacion);

    private static RequisitoBloqueanteDelCentro DeTrabajadorEn(Guid centro, Guid tipo) =>
        new(centro, tipo, AmbitoAplicacion.Trabajador, new CondicionesDeAccesoDelCentro(null, 0));

    private static RequisitoBloqueanteDelCentro DeEmpresaEn(Guid centro, Guid tipo) =>
        new(centro, tipo, AmbitoAplicacion.Empresa, new CondicionesDeAccesoDelCentro(null, 0));

    private static IReadOnlyList<ItemBandejaDto> ColaCompleta(
        IReadOnlyList<AlertaDto> alertas, IReadOnlyList<DocumentacionBloqueantePendienteDto> requisitos)
    {
        var fusionados = ObtenerBandejaGestorQueryHandler.Fusionar(alertas, [], requisitos, [], [], [], [], Hoy, 48, 24);
        return fusionados
            .Concat(ObtenerMiTrabajoAgregadoQueryHandler.MapearProximos(alertas))
            .ToList();
    }

    [Fact]
    public void El_mismo_Trabajador_y_Tipo_en_varios_Centros_da_una_fila_por_Centro_con_Id_distinto()
    {
        var trabajador = Guid.NewGuid();
        var tipo = Guid.NewGuid();
        var centros = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();

        var cola = ColaCompleta([.. centros.Select(c => Faltante(trabajador, tipo, c)), Faltante(trabajador, tipo, null)], []);

        cola.Should().HaveCount(5);
        IdDeFilaDeCola.Duplicados(cola, i => i.Id).Should().BeEmpty();
    }

    [Fact]
    public void Un_requisito_de_Empresa_bloqueante_por_varios_Trabajadores_y_varios_Centros_da_una_fila_por_Trabajador_y_Centro()
    {
        var (tipoEmpresa, tipoTrabajador) = (Guid.NewGuid(), Guid.NewGuid());
        var empresa = Guid.NewGuid();
        var centros = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var trabajadores = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();

        // Todos los Trabajadores de la Empresa asignados a todos los Centros: R1 (su propio Tipo) y R2 (el Tipo de la Empresa) a la vez.
        var asignaciones = centros.SelectMany(c => trabajadores.Select(t => new AsignacionParaBloqueo(c, t, empresa))).ToList();
        var bloqueos = CalculoBloqueoDeAccesoDeTrabajadores.Calcular(
            asignaciones,
            [.. centros.SelectMany(c => new[] { DeTrabajadorEn(c, tipoTrabajador), DeEmpresaEn(c, tipoEmpresa) })],
            documentos: [],
            Hoy);

        bloqueos.Should().HaveCount(centros.Count * trabajadores.Count * 2, "R1 + R2 por cada Trabajador y Centro");

        var cola = ColaCompleta([], [.. bloqueos.Select(Pendiente)]);

        cola.Should().HaveCount(bloqueos.Count);
        IdDeFilaDeCola.Duplicados(cola, i => i.Id).Should().BeEmpty();
    }

    [Fact]
    public void Una_Asignacion_repetida_del_mismo_Trabajador_al_mismo_Centro_no_repite_la_fila_del_requisito()
    {
        // La base lo impide (EXCLUDE de vigencias solapadas), pero el Id depende de que la entrada no repita el par:
        // el cálculo es único por (Centro, Trabajador, Tipo) por construcción, no por una restricción de otra capa.
        var (centro, trabajador, tipoEmpresa, tipoTrabajador, empresa) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var asignacion = new AsignacionParaBloqueo(centro, trabajador, empresa);

        var bloqueos = CalculoBloqueoDeAccesoDeTrabajadores.Calcular(
            [asignacion, asignacion],
            [DeTrabajadorEn(centro, tipoTrabajador), DeEmpresaEn(centro, tipoEmpresa)],
            documentos: [],
            Hoy);

        bloqueos.Should().HaveCount(2, "un requisito de Trabajador y uno de Empresa, no cuatro");
        IdDeFilaDeCola.Duplicados(ColaCompleta([], [.. bloqueos.Select(Pendiente)]), i => i.Id).Should().BeEmpty();
    }

    // ----------------------------------------------- 3. un productor que repite se deja ver

    private sealed class LoggerDeErrores : ILogger<ObtenerMiTrabajoAgregadoQueryHandler>
    {
        public List<string> Errores { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Errores.Add(formatter(state, exception));
        }
    }

    /// <summary>Cada Tenant devuelve las alertas que se le dieron; el resto de consultas, vacío.</summary>
    private sealed class MediadorConAlertas(Guid tenantA, IReadOnlyList<AlertaDto> alertasA, Guid tenantB, IReadOnlyList<AlertaDto> alertasB) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerClientesAutorizadosQuery)
            {
                IReadOnlyList<ClienteAutorizadoDto> tenants = [new(tenantA, "Tenant A", EsOrigen: false), new(tenantB, "Tenant B", EsOrigen: false)];
                return Task.FromResult((TResponse)(object)tenants);
            }

            object respuesta = request switch
            {
                ObtenerAlertasQuery => AmbitoTenantExplicito.TenantIdActual == tenantA ? alertasA : alertasB,
                ObtenerRevisionesIaPendientesQuery => new List<RevisionIaDocumentoDto>(),
                ObtenerDocumentacionBloqueantePendienteQuery => new List<DocumentacionBloqueantePendienteDto>(),
                ObtenerVisitasQuery => new ResultadoPaginado<VisitaListaDto>([], 0, 1, 200),
                ObtenerSugerenciasVisitaCorreoPendientesQuery => new List<SugerenciaVisitaCorreoPendienteDto>(),
                ObtenerDeteccionesPendientesQuery => new List<DeteccionPendienteDto>(),
                ObtenerAcreditacionesPorProveedorQuery => new List<ProveedorAcreditacionesDto>(),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }

    private sealed class EvaluacionDeAccesoSinUso : IEvaluacionDeAccesoPorCentroService
    {
        public Task<EvaluacionDeAccesoPorCentro> EvaluarAsync(IReadOnlyCollection<Guid>? centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static async Task<List<string>> ErroresDeMiTrabajoAsync(IReadOnlyList<AlertaDto> alertasA, IReadOnlyList<AlertaDto> alertasB)
    {
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        var configuracion = new ConfiguracionQueryContextFalso();
        configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
        var logger = new LoggerDeErrores();
        var handler = new ObtenerMiTrabajoAgregadoQueryHandler(
            new MediadorConAlertas(tenantA, alertasA, tenantB, alertasB), configuracion, new EmpresasQueryContextFalso(),
            new EvaluacionDeAccesoSinUso(), new AlcanceDatosServiceFalso(), logger);

        var resultado = await handler.Handle(new ObtenerMiTrabajoAgregadoQuery(), CancellationToken.None);

        resultado.NoConsultados.Should().BeEmpty("el log de Ids repetidos no puede venir de un Tenant caído");
        return logger.Errores;
    }

    [Fact]
    public async Task Mi_trabajo_no_registra_error_con_datos_que_generan_hermanos_y_si_lo_hace_cuando_un_productor_repite_un_Id()
    {
        var (trabajador, tipo) = (Guid.NewGuid(), Guid.NewGuid());
        var centroNorte = Guid.NewGuid();
        var sinRepetidos = new[] { Faltante(trabajador, tipo, centroNorte), Faltante(trabajador, tipo, Guid.NewGuid()) };

        // Mismas dimensiones en los dos Tenants: legal (el Id es único por Tenant).
        (await ErroresDeMiTrabajoAsync(sinRepetidos, sinRepetidos)).Should().BeEmpty();

        // Un productor que emite dos veces la misma fila: eso es un defecto y se registra.
        var errores = await ErroresDeMiTrabajoAsync([Faltante(trabajador, tipo, centroNorte), Faltante(trabajador, tipo, centroNorte)], sinRepetidos);
        errores.Should().ContainSingle().Which.Should().Contain("Id de fila repetidos").And.Contain($"alerta-{trabajador}-{tipo}-{centroNorte}");
    }
}
