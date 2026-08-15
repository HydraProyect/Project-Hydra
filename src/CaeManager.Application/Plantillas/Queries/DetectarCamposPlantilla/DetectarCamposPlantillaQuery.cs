using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using MediatR;

namespace CaeManager.Application.Plantillas.Queries.DetectarCamposPlantilla;

/// <summary>
/// Se dispara al subir el PDF original de una <see cref="PlantillaDocumentoVersion"/>
/// nueva (<see cref="OrigenPlantilla.Externa"/>), antes de que exista ningún
/// <see cref="PlantillaElemento"/> — propone candidatos para que el editor
/// visual (ADR-010 § 2.3, futuro PR) los pinte sobre la página rasterizada y
/// el gestor los mueva/acepte/descarte. No persiste nada: la confirmación es
/// un Command aparte, igual que <c>DetectarCamposDocumentoQuery</c> con
/// <c>AplicarDeteccion</c> (ADR-010 § 2.4, "la IA propone, el humano confirma").
/// </summary>
public record DetectarCamposPlantillaQuery(byte[] Contenido, FormatoOrigenPlantilla Formato)
    : IRequest<Result<IReadOnlyList<PlantillaElementoCandidatoDto>>>;

/// <summary>
/// Candidato transitorio — misma forma que el constructor de
/// <see cref="PlantillaElemento"/> menos <c>PlantillaDocumentoVersionId</c>
/// (todavía no existe ninguna versión guardada en este punto del flujo).
/// </summary>
public record PlantillaElementoCandidatoDto(
    TipoElementoPlantilla Tipo,
    int Pagina,
    double X,
    double Y,
    double Ancho,
    double Alto,
    string EtiquetaVisible,
    string? NombreCampoAcroForm,
    FuenteDatoPlantilla? FuenteDatoSugerida,
    int Confianza);
