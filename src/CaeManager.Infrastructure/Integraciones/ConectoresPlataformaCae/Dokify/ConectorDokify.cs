using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Domain.Integraciones;

namespace CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Dokify;

/// <summary>
/// Conector de la API pública de dokify, de solo lectura. Lo elige la puerta (<c>OperacionesConectorPlataformaCae</c>)
/// por <see cref="Codigo"/>, el del proveedor sembrado con el dominio <c>dokify.net</c> (grupo Once For All).
///
/// <para>
/// Nace sin poder llamar: el interruptor del proveedor está apagado en la semilla y, aunque se encendiera, ninguna de las
/// tres operaciones del puerto llama (ver sus atajos). Lo que sí sabe hacer, las lecturas de Empresas, Trabajadores,
/// máquinas y documentos, son métodos <c>internal</c> sin ningún consumidor de producto. No repite ninguna comprobación
/// de la puerta y no lee la base. <c>internal</c>: fuera de Infrastructure solo se alcanza como
/// <see cref="IConectorPlataformaCae"/>, por la puerta.
/// </para>
/// </summary>
internal sealed class ConectorDokify(DokifyClient cliente) : IConectorPlataformaCae
{
    public const string Codigo = "dokify";

    // shortcut: tope de páginas por listado, para que un servidor que ignore «page» no nos tenga pidiendo sin fin. Con
    // 50 por página son 25.000 filas; si una cuenta real lo supera, se pagina desde quien consume.
    public const int MaximoDePaginas = 500;

    public string CodigoProveedor => Codigo;

    // shortcut: el estado de un documento en dokify se lee por el CIF o NIF del elemento y el tipo de documento, y
    // SolicitudLecturaEstado solo trae los Id de TALVEG. Hasta que se decida cómo llegan esos datos, no se llama.
    public Task<ResultadoConector<EstadoDocumentoEnPlataforma>> LeerEstadoDocumentoAsync(
        CredencialApiConector credencial, SolicitudLecturaEstado solicitud, CancellationToken cancellationToken) =>
        Task.FromResult(ResultadoConector<EstadoDocumentoEnPlataforma>.Falla(MotivoUsoConector.DatosInsuficientes));

    // El alta de Trabajadores y la subida de documentos quedan fuera de este conector (decisión del 2026-11-20): es de solo
    // lectura. SinConector: TALVEG no tiene conector que haga esa operación con esta plataforma.
    public Task<ResultadoConector<ReferenciaEnPlataforma>> DarDeAltaTrabajadorAsync(
        CredencialApiConector credencial, SolicitudAltaTrabajador solicitud, CancellationToken cancellationToken) =>
        Task.FromResult(ResultadoConector<ReferenciaEnPlataforma>.Falla(MotivoUsoConector.SinConector));

    public Task<ResultadoConector<ReferenciaEnPlataforma>> SubirDocumentoAsync(
        CredencialApiConector credencial, SolicitudSubidaDocumento solicitud, CancellationToken cancellationToken) =>
        Task.FromResult(ResultadoConector<ReferenciaEnPlataforma>.Falla(MotivoUsoConector.SinConector));

    /// <summary>Todas las Empresas de la red del contratista (<c>clients/self/companies</c>), página a página.</summary>
    internal Task<ResultadoConector<IReadOnlyList<EmpresaDokify>>> ListarEmpresasAsync(
        CredencialApiConector credencial, CancellationToken cancellationToken) =>
        TodasLasPaginasAsync(pagina => cliente.ListarEmpresasAsync(credencial, pagina, DokifyClient.TamanoMaximoPagina, cancellationToken));

    internal Task<ResultadoConector<IReadOnlyList<TrabajadorDokify>>> ListarTrabajadoresAsync(
        CredencialApiConector credencial, string cifEmpresa, CancellationToken cancellationToken) =>
        TodasLasPaginasAsync(pagina =>
            cliente.ListarTrabajadoresDeEmpresaAsync(credencial, cifEmpresa, pagina, DokifyClient.TamanoMaximoPagina, cancellationToken));

    internal Task<ResultadoConector<MaquinaDokify>> ObtenerMaquinaAsync(
        CredencialApiConector credencial, string matricula, CancellationToken cancellationToken) =>
        cliente.ObtenerMaquinaAsync(credencial, matricula, cancellationToken);

    internal Task<ResultadoConector<EstadoGlobalDokify>> ObtenerEstadoAsync(
        CredencialApiConector credencial, ElementoDokify elemento, string vat, CancellationToken cancellationToken) =>
        cliente.ObtenerEstadoAsync(credencial, elemento, vat, cancellationToken);

    internal Task<ResultadoConector<IReadOnlyList<DocumentoDokify>>> ObtenerDocumentosAsync(
        CredencialApiConector credencial, ElementoDokify elemento, string vat, CancellationToken cancellationToken) =>
        cliente.ObtenerDocumentosAsync(credencial, elemento, vat, cancellationToken);

    internal Task<ResultadoConector<IReadOnlyList<AsignacionDokify>>> ObtenerAsignacionesAsync(
        CredencialApiConector credencial, ElementoDokify elemento, string vat, CancellationToken cancellationToken) =>
        cliente.ObtenerAsignacionesAsync(credencial, elemento, vat, cancellationToken);

    internal Task<ResultadoConector<DocumentoDescargadoDokify>> DescargarDocumentoAsync(
        CredencialApiConector credencial, ElementoDokify elemento, string vat, string tipoDocumentoId, CancellationToken cancellationToken) =>
        cliente.DescargarDocumentoAsync(credencial, elemento, vat, tipoDocumentoId, cancellationToken);

    /// <summary>
    /// Pide las páginas en orden hasta la última. Una página cuyo número no es el pedido es inesperada: un servidor que
    /// ignorase <c>page</c> devolvería siempre la misma y nunca terminaría.
    /// </summary>
    private static async Task<ResultadoConector<IReadOnlyList<T>>> TodasLasPaginasAsync<T>(
        Func<int, Task<ResultadoConector<PaginaDokify<T>>>> pedir)
    {
        var acumulado = new List<T>();
        for (var pagina = 0; pagina < MaximoDePaginas; pagina++)
        {
            var resultado = await pedir(pagina);
            if (resultado.Fallo is { } fallo)
                return ResultadoConector<IReadOnlyList<T>>.Falla(fallo);

            if (resultado.Valor.Numero != pagina)
                return ResultadoConector<IReadOnlyList<T>>.Falla(MotivoUsoConector.Inesperado);

            acumulado.AddRange(resultado.Valor.Datos);
            if (!resultado.Valor.HayMasPaginas)
                return ResultadoConector<IReadOnlyList<T>>.Exito(acumulado);
        }

        return ResultadoConector<IReadOnlyList<T>>.Falla(MotivoUsoConector.Inesperado);
    }
}
