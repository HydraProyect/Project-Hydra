namespace CaeManager.Application.Importacion;

/// <summary>
/// Lo que ya existe en el Tenant Y cae dentro del ámbito del acto (autoridad <see cref="AutoridadCargaMasiva.Acotada"/>).
/// Un analizador que recibe esto trata lo existente fuera del ámbito exactamente como lo inexistente: mismos avisos, mismas
/// asociaciones, mismas filas y mismo árbol, de modo que la vista previa no sirve de oráculo de existencia. Con autoridad
/// Total no hay instancia (null) y el analizador usa todo lo que existe.
/// </summary>
public sealed record ExistentesEnAmbito(
    HashSet<string> Clientes, HashSet<string> Empresas, HashSet<string> Centros, HashSet<string> Cifs, HashSet<string> Dnis,
    HashSet<(string Dni, string Centro)> Asignaciones, HashSet<string> ClientesCentros)
{
    public static string ClaveCentro(string cliente, string centro) => cliente + "\u001f" + centro;

    public bool Cliente(string razonSocial) => Clientes.Contains(razonSocial);
    public bool Empresa(string razonSocial) => Empresas.Contains(razonSocial);
    public bool Centro(string razonSocialCliente, string nombre) => ClientesCentros.Contains(ClaveCentro(razonSocialCliente, nombre));
    public bool Cif(string cif) => Cifs.Contains(cif.Trim());
    public bool Dni(string dni) => Dnis.Contains(dni);
}
