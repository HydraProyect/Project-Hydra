using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

public interface IParserDocumentoOficialRegistry
{
    IParserDocumentoOficial? Obtener(PerfilDocumentoOficial perfil);
}

/// <summary>
/// Un parser por perfil, resuelto por enum — sin reflexión ni descubrimiento
/// dinámico: los perfiles son un catálogo cerrado del dominio y añadir uno
/// nuevo pasa por tocar el enum de todas formas.
/// </summary>
public class ParserDocumentoOficialRegistry(IEnumerable<IParserDocumentoOficial> parsers) : IParserDocumentoOficialRegistry
{
    private readonly Dictionary<PerfilDocumentoOficial, IParserDocumentoOficial> _porPerfil =
        parsers.ToDictionary(p => p.Perfil);

    public IParserDocumentoOficial? Obtener(PerfilDocumentoOficial perfil) =>
        _porPerfil.GetValueOrDefault(perfil);
}
