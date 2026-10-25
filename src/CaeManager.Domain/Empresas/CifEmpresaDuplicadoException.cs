namespace CaeManager.Domain.Empresas;

/// <summary>Colisión del índice de CIF de Empresa; no representa otras restricciones.</summary>
public sealed class CifEmpresaDuplicadoException(IReadOnlyList<string> cifs, Exception innerException) : Exception("CIF de Empresa duplicado.", innerException)
{
    public IReadOnlyList<string> Cifs { get; } = cifs;

    public CifEmpresaDuplicadoException(string cif, Exception innerException) : this([cif], innerException) { }
}
