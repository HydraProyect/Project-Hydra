using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresaEmpleadora;

/// <summary>
/// Envía el lote de «Gestionar» de documentos de Trabajador a la Empresa que los emplea (decisión de Chris, 2026-10-18): al
/// contacto de reclamación de la Delegación (<paramref name="DelegacionEmpresaId"/>) o, sin ella, a la agenda de la Empresa.
/// Hermano de <see cref="EnviarReclamacionCommand"/> (el mismo lote, dirigido al Cliente del Centro) y con sus mismas reglas:
/// lo que se ofrece es lo que se acepta (misma ventana, Asignación activa, Centro con gestión CAE y alcance visible), todo
/// o nada, la selección manual de destinatarios se filtra contra lo resuelto y nada sale sin que el Gestor CAE lo confirme
/// en pantalla. La cola común —buzón, envío, registro y evento— es <see cref="IRegistroEnvioReclamacionService"/>.
///
/// <para>
/// <b>Cartera.</b> Escribir el historial y mandar un correo en nombre del Tenant sobre una Empresa es operar sobre ella: la
/// puerta es <see cref="AlcanceDatosServiceExtensions.EmpresaParaGestionVisibleAsync"/> (Asignación de Cartera), antes de
/// leer nada. Cada línea pedida tiene que ser de un Trabajador de esa Empresa que caiga en ese mismo lote.
/// </para>
/// </summary>
/// <param name="DelegacionEmpresaId">La Delegación del lote; null = el lote que va a la agenda de la Empresa.</param>
/// <param name="DocumentoIds">
/// Documentos de Trabajadores de esta Empresa y, en la reclamación por acceso (decisión de Chris, 2026-10-19), también de
/// la propia Empresa, que viajan en el mismo correo. Los de la Empresa se revalidan con la regla de
/// <c>EnviarReclamacionEmpresaCommand</c> (de esta Empresa, de ámbito Empresa, dentro de la ventana); un pendiente sin
/// Trabajador, con <c>ResolverParaEmpresaAsync</c>. Todo o nada.
/// </param>
public record EnviarReclamacionEmpresaEmpleadoraCommand(
    Guid EmpresaId,
    Guid? DelegacionEmpresaId,
    IReadOnlyList<Guid> DocumentoIds,
    IReadOnlyList<Guid>? ContactoIdsSeleccionados = null,
    IReadOnlyList<PendienteSinFecha>? Pendientes = null,
    IReadOnlyList<string>? CorreosAdicionales = null,
    IReadOnlyList<string>? CorreosEnCopia = null) : ICommand<EnvioReclamacionResultado>;

public class EnviarReclamacionEmpresaEmpleadoraCommandHandler(
    IEmpresasQueryContext empresasContext,
    IDocumentosQueryContext documentosContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    IAlcanceDatosService alcanceDatos,
    ReclamacionEmpresaEmpleadoraService empresaEmpleadora,
    IRegistroEnvioReclamacionService registroEnvio,
    IPendientesDeReclamacionService pendientesDeReclamacion)
    : IRequestHandler<EnviarReclamacionEmpresaEmpleadoraCommand, Result<EnvioReclamacionResultado>>
{
    public async Task<Result<EnvioReclamacionResultado>> Handle(EnviarReclamacionEmpresaEmpleadoraCommand request, CancellationToken cancellationToken)
    {
        var preparada = await PrepararAsync(request, cancellationToken);
        if (preparada.EsFallido)
            return Result.Fallo<EnvioReclamacionResultado>(preparada.Error);

        var p = preparada.Valor;
        var envio = await registroEnvio.EnviarYRegistrarAsync(
            new TitularReclamacion(p.TitularId, p.TitularRazonSocial, AmbitoAplicacion.Empresa, DocumentosDeTrabajadores: true),
            p.DocumentoIds, p.CorreosParaEnviar, p.Asunto, p.CuerpoHtml, cancellationToken, p.DocumentosQueFaltan, p.EnCopia);

        return envio.EsFallido
            ? Result.Fallo<EnvioReclamacionResultado>(envio.Error)
            : Result.Exito(new EnvioReclamacionResultado(p.DocumentoIds, p.CorreosParaEnviar, p.DocumentosQueFaltan.Count, p.EnCopia));
    }

    /// <summary>Lo que dice la columna «Trabajador» del correo en una fila de documentos de la propia Empresa.</summary>
    public const string RotuloEmpresa = "Empresa";

    /// <summary>Decide qué se envía y a quién, sin enviar: misma idea que <c>EnviarReclamacionCommandHandler.PrepararAsync</c>.</summary>
    public async Task<Result<ReclamacionPreparada>> PrepararAsync(EnviarReclamacionEmpresaEmpleadoraCommand request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
            return Result.Fallo<ReclamacionPreparada>(Error.Crear("Reclamacion.SinAcceso", "No tienes acceso a esta empresa."));

        var empresa = await empresasContext.Empresas.FirstOrDefaultAsync(e => e.Id == request.EmpresaId, cancellationToken);
        if (empresa is null)
            return Result.Fallo<ReclamacionPreparada>(Error.Crear("Reclamacion.EmpresaNoEncontrada", "No encontramos esta empresa."));

        var todosLosPedidos = request.Pendientes ?? [];
        if (request.DocumentoIds.Count == 0 && todosLosPedidos.Count == 0)
            return Result.Fallo<ReclamacionPreparada>(Error.Crear("Reclamacion.SinDocumentos", "Selecciona al menos un documento a reclamar."));

        // Reclamación por acceso: lo de la propia Empresa (sin Trabajador) se separa y se revalida con su propia regla.
        // Un «Ausente» de la Empresa no trae Trabajador; un «Sin confirmar» se reconoce por su Documento, que es de la Empresa.
        var sinConfirmarIds = todosLosPedidos.Where(p => p.DocumentoId is not null).Select(p => p.DocumentoId!.Value).ToList();
        var sinConfirmarDeEmpresa = sinConfirmarIds.Count == 0 ? [] : (await documentosContext.Documentos.Operativos()
            .Where(d => sinConfirmarIds.Contains(d.Id) && d.TrabajadorId == null && d.EmpresaId == request.EmpresaId)
            .Select(d => d.Id).ToListAsync(cancellationToken)).ToHashSet();
        var pedidosEmpresa = todosLosPedidos
            .Where(p => p.DocumentoId is { } id ? sinConfirmarDeEmpresa.Contains(id) : p.TrabajadorId is null)
            .ToList();
        var pedidos = todosLosPedidos.Except(pedidosEmpresa).ToList();
        var todosLosIds = request.DocumentoIds.Distinct().ToList();
        var hoy = DiaDeNegocio.Hoy();
        var filasEmpresa = await (
                from documento in documentosContext.Documentos.Reclamables(hoy)
                where todosLosIds.Contains(documento.Id)
                where documento.EmpresaId == request.EmpresaId
                where documento.TrabajadorId == null
                join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
                where tipoDocumento.AmbitoAplicacion == AmbitoAplicacion.Empresa
                select new
                {
                    DocumentoId = documento.Id,
                    TipoDocumentoId = tipoDocumento.Id,
                    TipoDocumentoNombre = tipoDocumento.Nombre,
                    documento.FechaVencimiento
                })
            .Distinct()
            .ToListAsync(cancellationToken);
        var idsEmpresa = filasEmpresa.Select(f => f.DocumentoId).ToHashSet();

        var trabajadorIdsDelLote = await empresaEmpleadora.TrabajadoresDelDestinoAsync(request.EmpresaId, request.DelegacionEmpresaId, cancellationToken);
        var idsSolicitados = todosLosIds.Where(id => !idsEmpresa.Contains(id)).ToList();

        // Mismo alcance y misma ventana que el lote (ObtenerLoteReclamacionQuery): un Id fuera de ellos falla igual que uno
        // ya renovado, sin decir que existe.
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);

        var filas = (await (
                from documento in documentosContext.Documentos.Reclamables(hoy)
                where idsSolicitados.Contains(documento.Id)
                where documento.TrabajadorId != null
                where trabajadorIdsDelLote.Contains(documento.TrabajadorId!.Value)
                where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
                join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
                join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
                join asignacion in asignacionesContext.Asignaciones on trabajador.Id equals asignacion.TrabajadorId
                where asignacion.FechaBaja == null
                where centroIdsVisibles == null || centroIdsVisibles.Contains(asignacion.CentroId)
                join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
                where centro.GestionCae != ModalidadGestionCae.SinGestionCae
                where clienteIdsVisibles == null || clienteIdsVisibles.Contains(centro.ClienteId)
                select new
                {
                    DocumentoId = documento.Id,
                    TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                    TipoDocumentoId = tipoDocumento.Id,
                    TipoDocumentoNombre = tipoDocumento.Nombre,
                    documento.FechaVencimiento
                })
                .Distinct()
                .ToListAsync(cancellationToken))
            .DistinctBy(f => f.DocumentoId)
            .ToList();

        var idsEncontrados = filas.Select(f => f.DocumentoId).ToHashSet();
        if (idsSolicitados.Exists(id => !idsEncontrados.Contains(id)))
        {
            return Result.Fallo<ReclamacionPreparada>(Error.Crear(
                "Reclamacion.DocumentosDesactualizados",
                "Algunos de los documentos seleccionados ya no son reclamables a esta empresa — puede que se hayan renovado, que el trabajador haya cambiado de delegación o que hayan salido de la ventana de reclamación. Actualiza la vista antes de volver a intentarlo."));
        }

        // Lo que se pide sin vencimiento: la misma regla que lo ofreció, acotada a los Trabajadores de este lote (todo o nada).
        IReadOnlyList<DocumentoPendienteDto> pendientes = [];
        if (pedidos.Count > 0)
        {
            if (!PendientesDeReclamacionService.PedidosBienFormados(pedidos, conTrabajador: true) || trabajadorIdsDelLote.Count == 0)
                return Result.Fallo<ReclamacionPreparada>(PendientesDeReclamacionService.ErrorDesactualizados());

            var disponibles = await pendientesDeReclamacion.ListarParaClientesAsync(
                centroId: null, clienteId: null, trabajadorIdsDelLote, tipoDocumentoIds: null, cancellationToken);
            var emparejados = PendientesDeReclamacionService.Emparejar(pedidos, disponibles.SelectMany(l => l.Pendientes));
            if (emparejados.EsFallido)
                return Result.Fallo<ReclamacionPreparada>(emparejados.Error);
            pendientes = emparejados.Valor;
        }

        IReadOnlyList<DocumentoPendienteDto> pendientesEmpresa = [];
        if (pedidosEmpresa.Count > 0)
        {
            if (pedidosEmpresa.Any(p => p.TrabajadorId is not null))
                return Result.Fallo<ReclamacionPreparada>(PendientesDeReclamacionService.ErrorDesactualizados());
            var resueltosEmpresa = await pendientesDeReclamacion.ResolverParaEmpresaAsync(request.EmpresaId, pedidosEmpresa, cancellationToken);
            if (resueltosEmpresa.EsFallido)
                return Result.Fallo<ReclamacionPreparada>(resueltosEmpresa.Error);
            pendientesEmpresa = [.. resueltosEmpresa.Valor.Select(p => p with { TrabajadorNombre = RotuloEmpresa })];
        }

        var destino = await empresaEmpleadora.ResolverDestinoAsync(
            request.EmpresaId, request.DelegacionEmpresaId,
            filas.Select(f => f.TipoDocumentoId).Concat(pendientes.Select(p => p.TipoDocumentoId))
                .Concat(filasEmpresa.Select(f => f.TipoDocumentoId)).Concat(pendientesEmpresa.Select(p => p.TipoDocumentoId))
                .Distinct().ToList(),
            cancellationToken);
        if (destino.EsFallido)
            return Result.Fallo<ReclamacionPreparada>(destino.Error);

        var resueltos = destino.Valor.Destinatarios;
        if (request.ContactoIdsSeleccionados is { Count: > 0 } seleccionados)
        {
            var contactoIdsResueltos = resueltos.Select(d => d.ContactoId).ToHashSet();
            if (seleccionados.Any(id => !contactoIdsResueltos.Contains(id)))
            {
                return Result.Fallo<ReclamacionPreparada>(Error.Crear(
                    "Reclamacion.ContactosDesactualizados",
                    "Alguno de los contactos seleccionados ya no es a quien se reclama esta documentación. Actualiza la vista antes de volver a intentarlo."));
            }

            resueltos = [.. resueltos.Where(d => seleccionados.Contains(d.ContactoId))];
        }

        if (resueltos.Count == 0)
        {
            return Result.Fallo<ReclamacionPreparada>(Error.Crear(
                "Reclamacion.SinDestinatario",
                "No hay a quién reclamar esta documentación: añade un contacto de reclamación a la delegación o un contacto en la ficha de la empresa."));
        }

        pendientes = [.. pendientes, .. pendientesEmpresa];
        var documentoIds = filas.Select(f => f.DocumentoId)
            .Concat(filasEmpresa.Select(f => f.DocumentoId))
            .Concat(pendientes.Where(p => p.DocumentoId is not null).Select(p => p.DocumentoId!.Value))
            .Distinct().ToList();
        var destinatarios = resueltos.Select(d => d.Email).Distinct().ToList();
        var extra = DestinatariosExtraDeReclamacion.Resolver(request.CorreosAdicionales, request.CorreosEnCopia, destinatarios);
        if (extra.EsFallido)
            return Result.Fallo<ReclamacionPreparada>(extra.Error);

        var saludo = destino.Valor.Destino.DelegacionEmpresaNombre is { } delegacion ? $"{empresa.RazonSocial} ({delegacion})" : empresa.RazonSocial;
        var asunto = EnviarReclamacionCommandHandler.ConstruirAsunto(empresa.RazonSocial);
        var cuerpoHtml = EnviarReclamacionCommandHandler.ConstruirCuerpoHtml(
            saludo,
            filas.Select(f => (f.TrabajadorNombre, f.TipoDocumentoNombre, f.FechaVencimiento!.Value))
                .Concat(filasEmpresa.Select(f => (RotuloEmpresa, f.TipoDocumentoNombre, f.FechaVencimiento!.Value))),
            pendientes);

        return Result.Exito(new ReclamacionPreparada(
            request.EmpresaId, empresa.RazonSocial, documentoIds, destinatarios,
            [.. resueltos.GroupBy(d => d.Email).Select(g => g.First())], asunto, cuerpoHtml, pendientes, extra.Valor,
            destino.Valor.Destino));
    }
}
