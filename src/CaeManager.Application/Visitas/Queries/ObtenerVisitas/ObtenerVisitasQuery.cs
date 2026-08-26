using CaeManager.Application.Centros;
using CaeManager.Application.Common;
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
    string? Busqueda, bool SoloActivas, bool? NotificadoCliente, bool SoloUrgentes = false, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false)
    : IRequest<ResultadoPaginado<VisitaListaDto>>;

public record VisitaListaDto(
    Guid Id,
    Guid CentroId,
    string CentroNombre,
    Guid ClienteId,
    string ClienteRazonSocial,
    Guid EmpresaId,
    string EmpresaRazonSocial,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    int TotalTrabajadores,
    bool DocumentacionCompleta,
    bool NotificadoCliente,
    OrigenVisita Origen,
    NivelUrgenciaVisita NivelUrgencia);

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
public class ObtenerVisitasQueryHandler(ICentrosQueryContext centrosContext, IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, ITiposDocumentoQueryContext tiposDocumentoContext, IVisitasQueryContext visitasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVisitasQuery, ResultadoPaginado<VisitaListaDto>>
{
    public async Task<ResultadoPaginado<VisitaListaDto>> Handle(ObtenerVisitasQuery request, CancellationToken cancellationToken)
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);

        // F3b — ClienteId ahora repunta contra Empresas (join independiente
        // del de EmpresaId de abajo: son dos roles distintos sobre la misma tabla).
        var consulta = from visita in visitasContext.Visitas
                       join centro in centrosContext.Centros on visita.CentroId equals centro.Id
                       join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
                       join empresa in empresasContext.Empresas on centro.EmpresaId equals empresa.Id
                       select new { visita, centro, cliente, empresa };

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            consulta = consulta.Where(x => centroIdsVisibles.Contains(x.centro.Id));

        if (request.SoloActivas)
            consulta = consulta.Where(x => x.visita.FechaFin >= hoy);

        if (request.NotificadoCliente is not null)
            consulta = consulta.Where(x => x.visita.NotificadoCliente == request.NotificadoCliente);

        if (request.SoloUrgentes)
        {
            // Mismo criterio que CalculadoraUrgenciaVisita, expresado en SQL
            // para poder filtrar antes de paginar: activa (FechaFin >= hoy,
            // cubre también "en curso") y su inicio cae dentro de la ventana
            // de aviso (en días completos — Visita no registra hora, ver el
            // comentario de la propia calculadora).
            var limiteAviso = hoy.AddDays(parametros.HorasAvisoVisita / 24);
            consulta = consulta.Where(x => x.visita.FechaFin >= hoy && x.visita.FechaInicio <= limiteAviso);
        }

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                x.centro.Nombre.ToUpper().Contains(busqueda) ||
                x.cliente.RazonSocial.ToUpper().Contains(busqueda) ||
                x.empresa.RazonSocial.ToUpper().Contains(busqueda));
        }

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery.
        // DocumentacionCompleta no se ordena aquí: se calcula en memoria más
        // abajo, después de paginar.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(VisitaListaDto.CentroNombre), false) => consulta.OrderBy(x => x.centro.Nombre),
            (nameof(VisitaListaDto.CentroNombre), true) => consulta.OrderByDescending(x => x.centro.Nombre),
            (nameof(VisitaListaDto.ClienteRazonSocial), false) => consulta.OrderBy(x => x.cliente.RazonSocial).ThenBy(x => x.visita.FechaInicio),
            (nameof(VisitaListaDto.ClienteRazonSocial), true) => consulta.OrderByDescending(x => x.cliente.RazonSocial).ThenBy(x => x.visita.FechaInicio),
            (nameof(VisitaListaDto.EmpresaRazonSocial), false) => consulta.OrderBy(x => x.empresa.RazonSocial).ThenBy(x => x.visita.FechaInicio),
            (nameof(VisitaListaDto.EmpresaRazonSocial), true) => consulta.OrderByDescending(x => x.empresa.RazonSocial).ThenBy(x => x.visita.FechaInicio),
            (nameof(VisitaListaDto.FechaInicio), true) => consulta.OrderByDescending(x => x.visita.FechaInicio),
            (nameof(VisitaListaDto.FechaFin), false) => consulta.OrderBy(x => x.visita.FechaFin),
            (nameof(VisitaListaDto.FechaFin), true) => consulta.OrderByDescending(x => x.visita.FechaFin),
            (nameof(VisitaListaDto.NotificadoCliente), false) => consulta.OrderBy(x => x.visita.NotificadoCliente).ThenBy(x => x.visita.FechaInicio),
            (nameof(VisitaListaDto.NotificadoCliente), true) => consulta.OrderByDescending(x => x.visita.NotificadoCliente).ThenBy(x => x.visita.FechaInicio),
            (nameof(VisitaListaDto.Origen), false) => consulta.OrderBy(x => x.visita.Origen).ThenBy(x => x.visita.FechaInicio),
            (nameof(VisitaListaDto.Origen), true) => consulta.OrderByDescending(x => x.visita.Origen).ThenBy(x => x.visita.FechaInicio),
            _ => consulta.OrderBy(x => x.visita.FechaInicio)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(x => x.visita.Id);

        var pagina = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new
            {
                x.visita.Id,
                CentroId = x.centro.Id,
                CentroNombre = x.centro.Nombre,
                ClienteId = x.cliente.Id,
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
                p.Id, p.CentroId, p.CentroNombre, p.ClienteId, p.ClienteRazonSocial, p.EmpresaId, p.EmpresaRazonSocial,
                p.FechaInicio, p.FechaFin, trabajadorIdsDeEstaVisita.Count,
                DocumentacionCompleta: empresaOk && trabajadoresOk,
                p.NotificadoCliente, p.Origen,
                NivelUrgencia: CalculadoraUrgenciaVisita.Calcular(
                    p.FechaInicio, p.FechaFin, hoy, parametros.HorasAvisoVisita, parametros.HorasCriticasVisita));
        }).ToList();

        return new ResultadoPaginado<VisitaListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
