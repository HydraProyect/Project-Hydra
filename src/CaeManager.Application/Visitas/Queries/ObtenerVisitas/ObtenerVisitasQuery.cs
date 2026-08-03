using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Visitas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Queries.ObtenerVisitas;

public record ObtenerVisitasQuery(
    string? Busqueda, bool SoloActivas, bool? NotificadoCliente, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<VisitaListaDto>>;

public record VisitaListaDto(
    Guid Id,
    string CentroNombre,
    string ClienteRazonSocial,
    string EmpresaRazonSocial,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    int TotalTrabajadores,
    bool DocumentacionCompleta,
    bool NotificadoCliente,
    OrigenVisita Origen);

/// <summary>
/// Igual que Dashboard/Alertas, el semáforo de cada Documento se calcula en
/// memoria con CalculadoraEstadoDocumento — nunca en SQL — para que nunca
/// pueda haber un resultado distinto entre pantallas. "Documentación
/// completa" de la Empresa exige que exista, para cada TipoDocumento
/// obligatorio de ámbito Empresa (EsObligatorio = true), al menos un
/// Documento de ese tipo en estado Vigente o NoAplica — un tipo obligatorio
/// sin ningún Documento cuenta como pendiente de gestionar, igual que uno
/// vencido. Para cada Trabajador de la visita se usa el criterio anterior
/// (al menos un Documento y todos Vigente/NoAplica) porque EsObligatorio
/// todavía no se aplica a documentos de Trabajador.
/// </summary>
public class ObtenerVisitasQueryHandler(ICentrosQueryContext centrosContext, IClientesQueryContext clientesContext, IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, ITiposDocumentoQueryContext tiposDocumentoContext, IVisitasQueryContext visitasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVisitasQuery, ResultadoPaginado<VisitaListaDto>>
{
    public async Task<ResultadoPaginado<VisitaListaDto>> Handle(ObtenerVisitasQuery request, CancellationToken cancellationToken)
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var consulta = from visita in visitasContext.Visitas
                       join centro in centrosContext.Centros on visita.CentroId equals centro.Id
                       join cliente in clientesContext.Clientes on centro.ClienteId equals cliente.Id
                       join empresa in empresasContext.Empresas on centro.EmpresaId equals empresa.Id
                       select new { visita, centro, cliente, empresa };

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            consulta = consulta.Where(x => centroIdsVisibles.Contains(x.centro.Id));

        if (request.SoloActivas)
            consulta = consulta.Where(x => x.visita.FechaFin >= hoy);

        if (request.NotificadoCliente is not null)
            consulta = consulta.Where(x => x.visita.NotificadoCliente == request.NotificadoCliente);

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                x.centro.Nombre.ToUpper().Contains(busqueda) ||
                x.cliente.RazonSocial.ToUpper().Contains(busqueda) ||
                x.empresa.RazonSocial.ToUpper().Contains(busqueda));
        }

        var total = await consulta.CountAsync(cancellationToken);

        var pagina = await consulta
            .OrderBy(x => x.visita.FechaInicio)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new
            {
                x.visita.Id,
                CentroNombre = x.centro.Nombre,
                ClienteRazonSocial = x.cliente.RazonSocial,
                EmpresaRazonSocial = x.empresa.RazonSocial,
                EmpresaId = x.empresa.Id,
                x.visita.FechaInicio,
                x.visita.FechaFin,
                x.visita.NotificadoCliente,
                x.visita.Origen
            })
            .ToListAsync(cancellationToken);

        if (pagina.Count == 0)
            return new ResultadoPaginado<VisitaListaDto>([], total, request.Pagina, request.TamanoPagina);

        var visitaIds = pagina.Select(p => p.Id).ToList();

        var trabajadoresPorVisita = await visitasContext.VisitasTrabajadores
            .Where(vt => visitaIds.Contains(vt.VisitaId))
            .Select(vt => new { vt.VisitaId, vt.TrabajadorId })
            .ToListAsync(cancellationToken);

        var trabajadorIdsImplicados = trabajadoresPorVisita.Select(t => t.TrabajadorId).Distinct().ToList();
        var empresaIdsImplicadas = pagina.Select(p => p.EmpresaId).Distinct().ToList();

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);

        var vencimientosTrabajadores = await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null && trabajadorIdsImplicados.Contains(d.TrabajadorId!.Value))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.FechaVencimiento })
            .ToListAsync(cancellationToken);

        var vencimientosEmpresas = await documentosContext.Documentos
            .Where(d => d.EmpresaId != null && empresaIdsImplicadas.Contains(d.EmpresaId!.Value))
            .Select(d => new { EmpresaId = d.EmpresaId!.Value, d.TipoDocumentoId, d.FechaVencimiento })
            .ToListAsync(cancellationToken);

        var tiposObligatoriosEmpresa = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Empresa && t.EsObligatorio)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        bool DocumentacionOk(IEnumerable<DateOnly?> fechasVencimiento)
        {
            var lista = fechasVencimiento.ToList();
            if (lista.Count == 0) return false;

            return lista.All(f =>
                CalculadoraEstadoDocumento.Calcular(f, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)
                    is EstadoDocumento.Vigente or EstadoDocumento.NoAplica);
        }

        bool TipoVigenteParaEmpresa(Guid empresaId, Guid tipoDocumentoId) =>
            vencimientosEmpresas
                .Where(v => v.EmpresaId == empresaId && v.TipoDocumentoId == tipoDocumentoId)
                .Any(v => CalculadoraEstadoDocumento.Calcular(v.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)
                    is EstadoDocumento.Vigente or EstadoDocumento.NoAplica);

        var elementos = pagina.Select(p =>
        {
            var trabajadorIdsDeEstaVisita = trabajadoresPorVisita
                .Where(t => t.VisitaId == p.Id)
                .Select(t => t.TrabajadorId)
                .ToList();

            var empresaOk = tiposObligatoriosEmpresa.Count == 0
                || tiposObligatoriosEmpresa.All(tipoId => TipoVigenteParaEmpresa(p.EmpresaId, tipoId));

            var trabajadoresOk = trabajadorIdsDeEstaVisita.All(trabajadorId =>
                DocumentacionOk(
                    vencimientosTrabajadores.Where(v => v.TrabajadorId == trabajadorId).Select(v => v.FechaVencimiento)));

            return new VisitaListaDto(
                p.Id, p.CentroNombre, p.ClienteRazonSocial, p.EmpresaRazonSocial,
                p.FechaInicio, p.FechaFin, trabajadorIdsDeEstaVisita.Count,
                DocumentacionCompleta: empresaOk && trabajadoresOk,
                p.NotificadoCliente, p.Origen);
        }).ToList();

        return new ResultadoPaginado<VisitaListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
