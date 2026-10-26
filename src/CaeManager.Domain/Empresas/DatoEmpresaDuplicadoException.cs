namespace CaeManager.Domain.Empresas;

/// <summary>Colisión de un índice único de identidad de Empresa.</summary>
public class DatoEmpresaDuplicadoException(IReadOnlyList<string> valores, bool razonSocial, Exception innerException)
    : Exception(razonSocial ? "Razón social de Empresa duplicada." : "CIF de Empresa duplicado.", innerException)
{
    public IReadOnlyList<string> Valores { get; } = valores;
    public bool EsRazonSocial { get; } = razonSocial;
}
