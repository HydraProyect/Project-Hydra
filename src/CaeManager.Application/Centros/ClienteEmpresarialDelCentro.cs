using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;

namespace CaeManager.Application.Centros;

/// <summary>
/// Un Centro con la Empresa en posición de Cliente empresarial a la que pertenece, por su razón social.
/// </summary>
public sealed record CentroConClienteEmpresarial(
    Guid CentroId, string NombreCentro, Guid ClienteEmpresarialId, string RazonSocialClienteEmpresarial);

/// <summary>
/// Única lectura de la pareja Cliente empresarial–Centro que comparten el analizador de la plantilla combinada y el
/// recorte de la vista previa al ámbito (<c>RecorteDeAnalisisAlAmbito</c>): los dos deciden con ella si un Centro de la
/// hoja «ya existe», así que leerla en un solo sitio evita que uno empareje con otra columna que el otro.
///
/// <para>
/// Deuda terminológica, aislada aquí: la columna <c>Centro.ClienteId</c> es el <c>Empresa.Id</c> del Cliente empresarial
/// del Centro (no la Empresa titular, que es <c>Centro.EmpresaId</c>); su estado final es la decisión D1 del plan de
/// migración de Cliente, abierta. Es un emparejamiento interno: un Centro cuya Empresa no exista o no se vea con los
/// filtros del contexto queda fuera, igual que antes en los dos consumidores.
/// </para>
/// </summary>
public static class ClienteEmpresarialDelCentro
{
    public static IQueryable<CentroConClienteEmpresarial> ConSuClienteEmpresarial(
        this IQueryable<Centro> centros, IQueryable<Empresa> empresas) =>
        from centro in centros
        join cliente in empresas on centro.ClienteId equals cliente.Id
        select new CentroConClienteEmpresarial(centro.Id, centro.Nombre, cliente.Id, cliente.RazonSocial);
}
