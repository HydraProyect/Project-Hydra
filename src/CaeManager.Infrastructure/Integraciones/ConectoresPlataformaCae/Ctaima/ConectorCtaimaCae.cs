using System.Globalization;
using System.Text.RegularExpressions;
using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Domain.Integraciones;

namespace CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Ctaima;

/// <summary>
/// Conector de la API oficial de CTAIMACAE.net. Lo elige la puerta
/// (<c>OperacionesConectorPlataformaCae</c>) por <see cref="Codigo"/>, que es el del proveedor sembrado
/// con el dominio <c>ctaimacae.net</c>: Twind y e-coordina son proveedores aparte aunque compartan grupo
/// (ver <see cref="ProveedorPlataformaCae"/>), y la definición OpenAPI de esta API nombra CTAIMACAE.net.
///
/// <para>
/// Nace sin poder llamar: el interruptor del proveedor está apagado en la semilla y, aunque se
/// encendiera, el <c>tenantId</c> que la API exige no llega todavía al conector (ver
/// <see cref="DarDeAltaTrabajadorAsync(CredencialApiConector, SolicitudAltaTrabajador, CancellationToken)"/>).
/// No repite ninguna comprobación de la puerta y no lee la base.
/// </para>
/// </summary>
public sealed partial class ConectorCtaimaCae(ClienteHttpCtaimaCae cliente) : IConectorPlataformaCae
{
    public const string Codigo = "ctaimacae-legacy";

    public const string TipoDocumentoNif = "NIF";
    public const string TipoDocumentoOtro = "Others";

    public string CodigoProveedor => Codigo;

    // shortcut: el estado de un documento en CTAIMACAE.net se lee por CIF del cliente, NIF del Trabajador y
    // requirementId (employee-invalid-documents, client-employee-requirements), y SolicitudLecturaEstado solo
    // trae los Id de TALVEG. Hasta que se decida cómo llegan esos datos, no se llama.
    public Task<ResultadoConector<EstadoDocumentoEnPlataforma>> LeerEstadoDocumentoAsync(
        CredencialApiConector credencial, SolicitudLecturaEstado solicitud, CancellationToken cancellationToken) =>
        Task.FromResult(ResultadoConector<EstadoDocumentoEnPlataforma>.Falla(MotivoUsoConector.DatosInsuficientes));

    // shortcut: client-employee-document exige CIF del cliente, NIF del Trabajador, CIF de la sociedad,
    // requirementId y fechas de expedición y caducidad, y SolicitudSubidaDocumento solo trae los Id de TALVEG y
    // el contenido. Hasta que se decida cómo llegan esos datos, no se llama.
    public Task<ResultadoConector<ReferenciaEnPlataforma>> SubirDocumentoAsync(
        CredencialApiConector credencial, SolicitudSubidaDocumento solicitud, CancellationToken cancellationToken) =>
        Task.FromResult(ResultadoConector<ReferenciaEnPlataforma>.Falla(MotivoUsoConector.DatosInsuficientes));

    // shortcut: ConexionPlataformaCae no guarda el tenantId de CTAIMACAE.net y la puerta solo pasa la
    // credencial; hasta que se decida de dónde sale, el alta por la puerta no llama.
    public Task<ResultadoConector<ReferenciaEnPlataforma>> DarDeAltaTrabajadorAsync(
        CredencialApiConector credencial, SolicitudAltaTrabajador solicitud, CancellationToken cancellationToken) =>
        DarDeAltaTrabajadorAsync(credencial, tenantIdCtaima: null, solicitud, cancellationToken);

    /// <summary>
    /// El alta completa en la gestión interna del contratista que identifica <paramref name="tenantIdCtaima"/>.
    /// Solo da de alta; vincular el Trabajador a un cliente o a un contrato es otra operación de la API.
    /// </summary>
    public async Task<ResultadoConector<ReferenciaEnPlataforma>> DarDeAltaTrabajadorAsync(
        CredencialApiConector credencial, string? tenantIdCtaima, SolicitudAltaTrabajador solicitud,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantIdCtaima)
            || string.IsNullOrWhiteSpace(solicitud.Dni)
            || string.IsNullOrWhiteSpace(solicitud.Nombre)
            || string.IsNullOrWhiteSpace(solicitud.Apellidos))
            return ResultadoConector<ReferenciaEnPlataforma>.Falla(MotivoUsoConector.DatosInsuficientes);

        var resultado = await cliente.CrearTrabajadorInternoAsync(
            credencial, tenantIdCtaima, PeticionDeAlta(solicitud), cancellationToken);

        if (resultado.Fallo is { } fallo)
            return ResultadoConector<ReferenciaEnPlataforma>.Falla(fallo);

        // employeeId es el id que piden las rutas /api/internal-employees/{id}; el guid, la alternativa.
        var creado = resultado.Valor;
        if (creado.EmployeeId > 0)
            return ResultadoConector<ReferenciaEnPlataforma>.Exito(
                new ReferenciaEnPlataforma(creado.EmployeeId.ToString(CultureInfo.InvariantCulture)));

        return string.IsNullOrWhiteSpace(creado.Guid)
            ? ResultadoConector<ReferenciaEnPlataforma>.Falla(MotivoUsoConector.Inesperado)
            : ResultadoConector<ReferenciaEnPlataforma>.Exito(new ReferenciaEnPlataforma(creado.Guid.Trim()));
    }

    /// <summary>
    /// DNI y NIE son NIF en CTAIMACAE.net; cualquier otro documento va como «Others», porque desde el texto
    /// no se distingue un pasaporte de otro identificador.
    /// </summary>
    public static TrabajadorInternoCtaimaPeticion PeticionDeAlta(SolicitudAltaTrabajador solicitud)
    {
        // shortcut: Apellidos llega en un solo campo y va entero a firstSurname. Partirlo en dos apellidos
        // rompería los compuestos («de la Fuente»); falta el dato separado en el Trabajador.
        var documento = solicitud.Dni.Trim().ToUpperInvariant();
        return new TrabajadorInternoCtaimaPeticion(
            IdentityCardNumber: documento,
            IdentityCardType: PatronNif().IsMatch(documento) ? TipoDocumentoNif : TipoDocumentoOtro,
            FirstName: solicitud.Nombre.Trim(),
            FirstSurname: solicitud.Apellidos.Trim(),
            SecondSurname: null,
            Enabled: true);
    }

    [GeneratedRegex("^([0-9]{8}|[XYZ][0-9]{7})[A-Z]$", RegexOptions.CultureInvariant)]
    private static partial Regex PatronNif();
}
