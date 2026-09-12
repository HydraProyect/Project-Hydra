using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Tenants;
using MediatR;

namespace CaeManager.Web.Tests;

/// <summary>
/// Ítems y dobles compartidos por los tests de «Mi trabajo» (<c>/bandeja</c>).
/// Viven aquí y no duplicados en cada fichero porque <c>ItemBandejaDto</c>
/// tiene diez parámetros posicionales: repetir la construcción entera por caso
/// hace que el dato de prueba tape lo que el caso quiere decir.
/// </summary>
internal static class BandejaDatosDePrueba
{
    /// <summary>Un ítem del tipo pedido, ya colgado de un Cliente para que <c>Agrupar</c> lo meta en un grupo (ver ClaveGrupo: sin Cliente ni Empresa cae en SinGrupo).</summary>
    public static ItemBandejaDto Item(
        string id,
        TipoItemBandeja tipo,
        Guid clienteId,
        string clienteNombre,
        DateOnly? fecha = null,
        Guid? trabajadorId = null,
        string? trabajadorNombre = null,
        Guid? tipoDocumentoId = null) =>
        new(
            Id: id,
            Tipo: tipo,
            Titulo: $"Documento {id}",
            Subtitulo: clienteNombre,
            Fecha: fecha,
            TrabajadorId: trabajadorId,
            CentroId: null,
            DocumentoId: null,
            TipoDocumentoId: tipoDocumentoId,
            RequisitoId: null,
            ClienteId: clienteId,
            ClienteNombre: clienteNombre,
            TrabajadorNombre: trabajadorNombre);

    /// <summary>
    /// Doble de <see cref="IMediator"/> para la pantalla: solo responde a las
    /// dos consultas que hace (<see cref="ObtenerBandejaAgrupadaQuery"/> y
    /// <see cref="ObtenerPerfilVocabularioActualQuery"/>) y revienta con
    /// cualquier otra. Es a propósito: si mañana la pantalla añade una tercera
    /// consulta, estos tests tienen que enterarse en vez de seguir en verde
    /// sobre un <c>default</c> silencioso.
    /// </summary>
    internal sealed class MediatorDeLaBandeja(params ItemBandejaDto[] items) : IMediator
    {
        public PerfilVocabularioTenant Perfil { get; init; } = PerfilVocabularioTenant.ClienteDirecto;

        /// <summary>Token con el que viajó cada consulta de la bandeja, en orden de llegada — para comprobar que se cancelan al retirar la pantalla.</summary>
        public List<CancellationToken> TokensDeCarga { get; } = [];

        /// <summary>
        /// Retiene la consulta de la bandeja número N (1 = la de
        /// <c>OnInitializedAsync</c>) devolviendo una promesa que el test
        /// resuelve cuando quiere. Lo que NO se retiene responde al instante
        /// con <see cref="ObtenerBandejaAgrupadaQueryHandler.Agrupar"/> sobre
        /// los ítems de esa entrada.
        /// </summary>
        public Dictionary<int, TaskCompletionSource<IReadOnlyList<ItemBandejaDto>>> Retenidas { get; } = [];

        /// <summary>Ítems que devuelve una carga no retenida.</summary>
        public IReadOnlyList<ItemBandejaDto> Items { get; set; } = items;

        public int Cargas { get; private set; }

        /// <summary>
        /// Cuántas veces se pidió el perfil de vocabulario. Es la SEGUNDA
        /// consulta de cada carga, así que su contador dice cuántas cargas han
        /// pasado ya de su primer <c>await</c> — la señal que necesita el caso
        /// de carreras para saber que la respuesta tardía llegó a ejecutarse,
        /// en vez de dar por buena una ausencia sin haber mirado.
        /// </summary>
        public int Perfiles { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerBandejaAgrupadaQuery:
                    TokensDeCarga.Add(cancellationToken);
                    Cargas++;
                    return Retenidas.TryGetValue(Cargas, out var retenida)
                        ? Agrupar<TResponse>(retenida.Task)
                        : Task.FromResult((TResponse)(object)ObtenerBandejaAgrupadaQueryHandler.Agrupar(Items));
                case ObtenerPerfilVocabularioActualQuery:
                    Perfiles++;
                    return Task.FromResult((TResponse)(object)Perfil);
                default:
                    throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");
            }
        }

        private static async Task<TResponse> Agrupar<TResponse>(Task<IReadOnlyList<ItemBandejaDto>> pendiente) =>
            (TResponse)(object)ObtenerBandejaAgrupadaQueryHandler.Agrupar(await pendiente);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
