using CaeManager.Application.Cumplimiento.AceptacionContractual;

namespace CaeManager.Infrastructure.Cumplimiento;

/// <summary>
/// Las versiones publicadas de los documentos contractuales del alta autónoma (#2009).
///
/// <para>
/// <b>Vacío a propósito.</b> Qué documentos se aceptan en el alta, su texto y su versión los fija Chris
/// con el abogado; hoy son borradores que no se publican. Sin versiones publicadas,
/// <see cref="RegistroAceptacionContractualAlta"/> rechaza cualquier aceptación: el alta falla cerrada,
/// y además sigue detrás de la bandera <c>AltaAutonoma:Habilitada</c>, apagada (DA-10).
/// </para>
///
/// <para>
/// Para publicar una versión: añadir aquí su clave, título, versión, inicio de vigencia en UTC, si es
/// obligatoria en el alta y su texto exacto, el mismo que se muestra al aceptar. Nunca se borra ni se
/// edita una versión ya publicada: un cambio de texto es una versión nueva con su propia vigencia, porque
/// la evidencia guardada apunta al hash del texto que se aceptó.
/// </para>
/// </summary>
public sealed class CatalogoDocumentosContractuales : ICatalogoDocumentosContractuales
{
    private static readonly IReadOnlyList<DocumentoContractualPublicado> Versiones = [];

    public CatalogoDocumentosContractuales() => ResolucionDocumentosContractuales.ExigirCoherente(Versiones);

    public IReadOnlyList<DocumentoContractualPublicado> Publicados => Versiones;
}
