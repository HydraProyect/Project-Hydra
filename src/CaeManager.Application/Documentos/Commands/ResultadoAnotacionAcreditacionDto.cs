using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.Commands;

/// <summary>
/// Lo que una anotación (marcar aceptado o anotar vigencia) sobrescribió y la versión en
/// la que dejó la acreditación. Es el recibo que el aviso «Deshacer» devuelve a
/// <c>RestaurarAnotacionAcreditacionCommand</c>. Lo calcula el handler de la anotación, no
/// la pantalla: el valor previo es el que de verdad había en base de datos, no el que la
/// pantalla tenía cargado.
/// </summary>
public record ResultadoAnotacionAcreditacionDto(
    Guid AcreditacionId, EstadoAcreditacion EstadoPrevio, VigenciaEnPlataforma VigenciaPrevia, Guid VersionResultante);
