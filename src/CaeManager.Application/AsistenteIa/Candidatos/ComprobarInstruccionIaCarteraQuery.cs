using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Candidatos;

/// <summary>
/// Nivel 0 (DEC-33) sobre <b>toda la cartera</b> del Gestor CAE, no solo sobre el
/// Tenant de la pantalla. Lo que el Gestor CAE escribe al asistente puede nombrar a
/// personas de cualquier Tenant de su cartera, y ese texto viaja al proveedor de IA
/// entero: no hay forma fiable de saber en local a qué Tenant pertenece un nombre
/// (un filtro por nombres da falsos negativos). Mientras no haya decisión jurídica
/// definitiva, la medida interina es <b>fallar cerrado</b>: si algún Tenant de la
/// cartera no tiene instrucción de tratamiento con IA vigente, el texto no sale
/// (<see cref="InstruccionIaCarteraDto.ErrorSiFalta"/>).
/// <para>
/// Los Tenants salen de <see cref="ObtenerClientesAutorizadosQuery"/>, <b>todos</b>: a
/// diferencia de <see cref="ObtenerCandidatosAsistenteQueryHandler"/>, aquí no se
/// descarta ninguno por alcance cero. Ese criterio (sin acceso total y sin Clientes
/// empresariales en cartera) también lo cumple una Asignación de Cartera universal
/// en un Tenant sin Clientes empresariales, que sí ve Centros y Trabajadores; ante
/// la duda, cuenta. La instrucción de cada uno se lee <b>dentro de su
/// <see cref="AmbitoTenantExplicito"/></b>: fuera de él, el filtro de Tenant y RLS
/// esconden la instrucción de otro Tenant y la comprobación la daría siempre por
/// ausente.
/// </para>
/// </summary>
public record ComprobarInstruccionIaCarteraQuery : IRequest<InstruccionIaCarteraDto>;

/// <summary>Los Tenants de la cartera, separados por si tienen instrucción de tratamiento con IA vigente.</summary>
public record InstruccionIaCarteraDto(
    IReadOnlyList<TenantDeCarteraDto> ConInstruccion,
    IReadOnlyList<TenantDeCarteraDto> SinInstruccion)
{
    public const string CodigoError = "AsistenteIa.CarteraSinInstruccion";

    /// <summary>El error que impide enviar el texto al proveedor, o null si toda la cartera tiene instrucción.</summary>
    public Error? ErrorSiFalta() => SinInstruccion.Count == 0
        ? null
        : Error.Crear(CodigoError, SinInstruccion.Count == 1
            ? $"No se ha enviado tu mensaje al asistente: {SinInstruccion[0].Nombre} está en tu cartera y no tiene una instrucción de tratamiento con IA vigente. Tu mensaje podría nombrar a personas de ese Tenant."
            : $"No se ha enviado tu mensaje al asistente: {string.Join(", ", SinInstruccion.Select(t => t.Nombre))} están en tu cartera y no tienen una instrucción de tratamiento con IA vigente. Tu mensaje podría nombrar a personas de esos Tenants.");
}

public class ComprobarInstruccionIaCarteraQueryHandler(
    IMediator mediator, IInstruccionTratamientoIaService instruccionTratamientoIa)
    : IRequestHandler<ComprobarInstruccionIaCarteraQuery, InstruccionIaCarteraDto>
{
    public async Task<InstruccionIaCarteraDto> Handle(ComprobarInstruccionIaCarteraQuery request, CancellationToken cancellationToken)
    {
        var autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);

        var con = new List<TenantDeCarteraDto>();
        var sin = new List<TenantDeCarteraDto>();
        foreach (var tenant in autorizados)
        {
            using (AmbitoTenantExplicito.Establecer(tenant.TenantId))
            {
                var deCartera = new TenantDeCarteraDto(tenant.TenantId, tenant.Nombre, tenant.EsOrigen);
                (await instruccionTratamientoIa.EstaHabilitadaAsync(tenant.TenantId, cancellationToken) ? con : sin).Add(deCartera);
            }
        }

        return new InstruccionIaCarteraDto(con, sin);
    }
}
