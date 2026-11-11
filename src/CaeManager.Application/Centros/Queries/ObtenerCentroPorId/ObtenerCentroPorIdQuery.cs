using CaeManager.Domain.Centros;
using CaeManager.Domain.Plataforma;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerCentroPorId;

public record ObtenerCentroPorIdQuery(Guid Id) : IRequest<CentroDetalleDto?>;

public record CentroDetalleDto(
    Guid Id,
    Guid ClienteId,
    string ClienteRazonSocial,
    Guid EmpresaId,
    string EmpresaRazonSocial,
    string Nombre,
    string? CodigoCentro,
    string? Direccion,
    string? Contacto,
    DateOnly? ContratoVigenteHasta,
    Guid Version,
    ModalidadGestionCae GestionCae = ModalidadGestionCae.ConGestionCae,
    string? HorarioAcceso = null,
    string? ControlAccesosNombre = null,
    string? ControlAccesosTelefono = null,
    string? TecnicoPrevencionNombre = null,
    string? TecnicoPrevencionTelefono = null,
    string? TecnicoPrevencionCorreo = null,
    bool? TrabajosEnAltura = null,
    bool? EspaciosConfinados = null,
    bool? AtmosferasExplosivas = null,
    bool FichaVisible = false,
    string? Notas = null,
    bool NotaInternaVisible = false,
    DateTime? CreadoEnUtc = null,
    PapelDeEntradaDto? PapelDeEntrada = null);

public class ObtenerCentroPorIdQueryHandler(ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos, IPoliticaNotaInterna politicaNotaInterna)
    : IRequestHandler<ObtenerCentroPorIdQuery, CentroDetalleDto?>
{
    public async Task<CentroDetalleDto?> Handle(ObtenerCentroPorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.Id, cancellationToken)) return null;

        // Los contactos personales de la ficha siguen la misma política que Empresa 360.
        // El Drawer lee y escribe con el mismo ámbito de gestión. Una clave de la API pública entra con rol
        // Consulta y pasaría esta política: por eso la API v1 publica CentroApiDetalleDto, sin la ficha.
        // La «Nota interna» (Centro.Notas) sigue el mismo criterio que la de Empresa 360 (ObtenerEmpresaPorIdQuery): solo
        // viaja a quien la lee según IPoliticaNotaInterna; al usuario de Cliente le llegan Notas = null y
        // NotaInternaVisible = false. La API v1 tampoco la publica (CentroApiDetalleDto).
        var conNotaInterna = await politicaNotaInterna.PuedeLeerAsync(cancellationToken);
        var conFicha = conNotaInterna
            && await alcanceDatos.CentroParaGestionVisibleAsync(request.Id, cancellationToken);

        var fila = await (
            from centro in centrosContext.Centros
                // Centro.ClienteId repunta contra Empresas desde F3b (CentroConfiguration):
                // "Cliente" es una Empresa contraparte (Empresa.CrearComoCliente).
            join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
            join empresa in empresasContext.Empresas on centro.EmpresaId equals empresa.Id
            where centro.Id == request.Id
            // Papel por Centro: el ancla y su X se leen en esta misma sentencia (sin SQL aparte para la ficha), en el
            // Tenant propietario del Centro y con lo que fijó Centro.AnclarRelacionDeEntrada vuelto a comprobar.
            // Una arista anclada que se cerró después (VigenciaHasta informada, RelacionEmpresarial.EstaVigente falso)
            // no da papel (DECISION_LOG, 2026-11-11, punto 11 b).
            // Si X está o no en la cartera de quien mira lo decide PapelDeEntradaEnCentro.ResolverAsync.
            from ancla in empresasContext.RelacionesEmpresariales
                .Where(r => r.Id == centro.RelacionEmpresarialEntradaId
                    && r.TenantId == centro.TenantId
                    && r.ProveedoraId == centro.EmpresaId
                    && r.ClienteId != centro.ClienteId
                    && r.VigenciaHasta == null)
                .DefaultIfEmpty()
            from contratistaPrincipal in empresasContext.Empresas
                .Where(x => ancla != null && x.Id == ancla.ClienteId)
                .DefaultIfEmpty()
            select new
            {
                Detalle = new CentroDetalleDto(
                centro.Id, centro.ClienteId, cliente.RazonSocial, centro.EmpresaId, empresa.RazonSocial, centro.Nombre,
                centro.CodigoCentro, centro.Direccion, centro.Contacto, centro.ContratoVigenteHasta, centro.Version,
                centro.GestionCae,
                conFicha ? centro.HorarioAcceso : null,
                conFicha ? centro.ControlAccesosNombre : null,
                conFicha ? centro.ControlAccesosTelefono : null,
                conFicha ? centro.TecnicoPrevencionNombre : null,
                conFicha ? centro.TecnicoPrevencionTelefono : null,
                conFicha ? centro.TecnicoPrevencionCorreo : null,
                conFicha ? centro.TrabajosEnAltura : null,
                conFicha ? centro.EspaciosConfinados : null,
                conFicha ? centro.AtmosferasExplosivas : null, conFicha,
                conNotaInterna ? centro.Notas : null, conNotaInterna, centro.CreadoEnUtc),
                ContratistaPrincipalId = ancla != null ? (Guid?)ancla.ClienteId : null,
                ContratistaPrincipalRazonSocial = contratistaPrincipal != null ? contratistaPrincipal.RazonSocial : null,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (fila is null) return null;

        var papel = await PapelDeEntradaEnCentro.ResolverAsync(
            fila.ContratistaPrincipalId, fila.ContratistaPrincipalRazonSocial, alcanceDatos, cancellationToken);
        return fila.Detalle with { PapelDeEntrada = papel };
    }
}
