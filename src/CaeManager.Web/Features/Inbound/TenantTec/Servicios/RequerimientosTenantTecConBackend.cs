using CaeManager.Application.Inbound.AutorizacionesLectura.Queries.ObtenerRequerimientosTec;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Inbound.TenantTec.Recursos;
using MediatR;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Inbound.TenantTec.Servicios;

/// <summary>
/// Requerimientos del Tenant TEC con datos reales (demo Inbound, P1): la pantalla recibe lo que calcula
/// <see cref="ObtenerRequerimientosTecQuery"/> (requisito de la lectura inversa X-2 × sujeto asignado × documento vigente
/// propio). Sin veredictos todavía (P0b): un requerimiento está «Pendiente de subir» o, si hay documento vigente propio del
/// tipo, «Pendiente de validar»; nunca Validado, Rechazado ni Incompleto, y sin motivo. Sin historial ni lectura automática.
/// Los errores suben a la pantalla, que los enseña como aviso sin el mensaje.
/// </summary>
public sealed class RequerimientosTenantTecConBackend(IMediator mediator, IStringLocalizer<TextosInboundTenantTec> textos)
    : IRequerimientosTenantTec
{
    public bool Disponible => true;

    public async Task<IReadOnlyList<RequerimientoTec>> ListarAsync(CancellationToken ct = default)
    {
        var filas = await mediator.Send(new ObtenerRequerimientosTecQuery(), ct);
        return filas.Select(Mapear).ToList();
    }

    private RequerimientoTec Mapear(RequerimientoTecDto f)
    {
        var conDocumento = f.Estado == EstadoRequerimientoTecSinVeredicto.PendienteDeValidar;
        return new RequerimientoTec(
            f.Id,
            f.ClienteEmpresarialNombre,
            f.TipoDocumentoNombre,
            f.EmpresaNombre,
            PapelEmpresa: null,
            f.Sujeto == SujetoRequerimientoTec.Empresa ? EntidadRequerimientoTec.Empresa : EntidadRequerimientoTec.Trabajador,
            f.SujetoNombre,
            AmbitoEntidad: null,
            f.CentroNombre,
            conDocumento ? EstadoRequerimientoTec.PendienteDeValidar : EstadoRequerimientoTec.PendienteDeSubir,
            VigenciaDe(f.EstadoDocumento),
            f.DocumentoVencimiento,
            f.DocumentosVigentes,
            Motivo: null,
            Falta: null,
            f.Criterio,
            conDocumento ? AccionRequerimientoTec.Ninguna : AccionRequerimientoTec.Subir,
            conDocumento,
            f.EsAptitudMedica,
            Lectura: [],
            [new NivelRequerimientoTec(NivelDe(f.CapaPlantilla), f.TipoDocumentoNombre, f.DocumentoVencimiento)],
            Historial: []);
    }

    private string NivelDe(string capa) => capa switch
    {
        "DeCentro" => textos["NivelCentro"].Value,
        "ExigenciasClienteEmpresarial" => textos["NivelClienteEmpresarial"].Value,
        _ => capa,
    };

    private static VigenciaRequerimientoTec VigenciaDe(EstadoDocumento? estado) => estado switch
    {
        EstadoDocumento.SinCaducidad or EstadoDocumento.Vigente => VigenciaRequerimientoTec.Vigente,
        EstadoDocumento.Proximo => VigenciaRequerimientoTec.Proximo,
        EstadoDocumento.Urgente => VigenciaRequerimientoTec.Urgente,
        EstadoDocumento.Vencido => VigenciaRequerimientoTec.Vencido,
        _ => VigenciaRequerimientoTec.SinVigencia,
    };
}
