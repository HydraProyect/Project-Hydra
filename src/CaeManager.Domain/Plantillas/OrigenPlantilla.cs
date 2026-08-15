namespace CaeManager.Domain.Plantillas;

/// <summary>
/// Discriminador único que separa dos capacidades hermanas con la misma
/// forma de datos pero alcance distinto (ADR-010 § 1.3, § 2.2):
/// <see cref="Externa"/> es el motor de este ADR (un centro entrega un PDF
/// que hay que cumplimentar); <see cref="TalvegPropio"/> es la biblioteca de
/// formatos propios de Hydra (ADR-007, pendiente de aprobar aparte) — su
/// mapeo campo→dato vive en código (HTML+CSS revisado en PR), no en
/// <see cref="PlantillaElemento"/>.
/// </summary>
public enum OrigenPlantilla
{
    TalvegPropio,
    Externa
}
