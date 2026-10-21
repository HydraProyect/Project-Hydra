using CaeManager.Application.Common;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerDocumentacionExigidaDeCliente;

/// <summary>
/// Alimenta la pestaña «Documentación» de Cliente 360: lo que el Cliente empresarial exige para acceder a sus Centros,
/// agregado por tipo de documento. Es el mismo universo que la pestaña «Requisitos» de Centro 360
/// (<see cref="ObtenerDocumentacionRequeridaDeCentroQuery"/>, con la regla común de
/// <see cref="DocumentacionRequeridaDeCentros"/>) unido sobre los Centros con gestión CAE del Cliente empresarial: un
/// tipo entra si algún Centro lo pide hoy, cuenta en cuántos y bloquea el acceso si algún Centro lo bloquea. Un Centro
/// sin gestión CAE no exige nada.
///
/// Una sola consulta en vez de una por Centro: el catálogo y las filas explícitas se leen una vez, sin tope de Centros.
/// Respeta la Asignación de Cartera: un Cliente empresarial fuera de cartera devuelve <c>null</c> (como si no existiera)
/// y solo cuentan los Centros visibles (<see cref="IAlcanceDatosService.ObtenerCentroIdsVisiblesAsync"/>).
/// </summary>
public record ObtenerDocumentacionExigidaDeClienteQuery(Guid ClienteEmpresarialId) : IRequest<DocumentacionExigidaClienteDto?>;

/// <param name="CentrosConGestionCae">Centros visibles del Cliente empresarial con gestión CAE: el «M» de «En N de M Centros».</param>
/// <param name="Requisitos">Primero lo que se exige a la Empresa y después lo de sus Trabajadores; dentro, el orden del catálogo.</param>
public record DocumentacionExigidaClienteDto(int CentrosConGestionCae, IReadOnlyList<RequisitoExigidoClienteDto> Requisitos);

/// <param name="Centros">En cuántos de esos Centros aplica hoy.</param>
/// <param name="BloqueaAcceso">Si al menos uno de esos Centros bloquea el acceso por este tipo.</param>
/// <param name="EsCertificadoTgss">
/// Si es el certificado de estar al corriente con la Seguridad Social (perfil oficial de la TGSS): la pestaña lo pinta
/// con el estado de cada Empresa en vez de como un requisito más, para no mostrarlo dos veces.
/// </param>
public record RequisitoExigidoClienteDto(
    Guid TipoDocumentoId, string Nombre, AmbitoAplicacion AmbitoAplicacion, int Centros, bool BloqueaAcceso,
    bool EsCertificadoTgss = false);

public class ObtenerDocumentacionExigidaDeClienteQueryHandler(
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDocumentacionExigidaDeClienteQuery, DocumentacionExigidaClienteDto?>
{
    public async Task<DocumentacionExigidaClienteDto?> Handle(
        ObtenerDocumentacionExigidaDeClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteEmpresarialId, cancellationToken))
            return null;

        var centros = centrosContext.Centros
            .Where(c => c.ClienteId == request.ClienteEmpresarialId && c.GestionCae != ModalidadGestionCae.SinGestionCae);

        // null = sin restricción; una lista (aunque vacía) es la cartera del usuario.
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            centros = centros.Where(c => centroIdsVisibles.Contains(c.Id));

        var centroIds = await centros.Select(c => c.Id).ToListAsync(cancellationToken);
        if (centroIds.Count == 0)
            return new DocumentacionExigidaClienteDto(0, []);

        var tipos = await DocumentacionRequeridaDeCentros.CargarCatalogoAsync(tiposDocumentoContext, cancellationToken);
        var filas = await DocumentacionRequeridaDeCentros.CargarFilasAsync(tiposDocumentoContext, centroIds, cancellationToken);

        var requisitos = tipos
            .Select(tipo =>
            {
                var exigentes = centroIds.Where(centroId => DocumentacionRequeridaDeCentros.Aplica(filas, tipo, centroId)).ToList();
                return new RequisitoExigidoClienteDto(
                    tipo.Id, tipo.Nombre, tipo.AmbitoAplicacion, exigentes.Count,
                    exigentes.Any(centroId => DocumentacionRequeridaDeCentros.BloqueaAcceso(filas, tipo.Id, centroId)),
                    tipo.EsCertificadoTgss);
            })
            .Where(r => r.Centros > 0)
            // Estable: dentro de cada ámbito se conserva el orden del catálogo.
            .OrderBy(r => r.AmbitoAplicacion == AmbitoAplicacion.Empresa ? 0 : 1)
            .ToList();

        return new DocumentacionExigidaClienteDto(centroIds.Count, requisitos);
    }
}
