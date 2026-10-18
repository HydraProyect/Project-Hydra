using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;

namespace CaeManager.Application.Empresas;

public interface IEmpresasQueryContext
{
    IQueryable<Empresa> Empresas { get; }
    IQueryable<CredencialAccesoEmpresa> CredencialesAccesoEmpresa { get; }

    /// <summary>
    /// Única fuente de los vínculos empresariales desde F4.2c; las tres
    /// tablas puente legacy que la precedieron se retiraron en el cierre de
    /// F4. Filtrar siempre por <c>VigenciaHasta == null</c>: a diferencia
    /// del borrado físico que hacían aquellas tablas, aquí una relación
    /// cerrada no desaparece — sigue siendo historia consultable.
    /// </summary>
    IQueryable<RelacionEmpresarial> RelacionesEmpresariales { get; }

    /// <summary>La Plataforma CAE de cada Cliente empresarial (caja «Plataforma CAE» de Cliente 360).</summary>
    IQueryable<PlataformaCaeClienteEmpresarial> PlataformasCaeClienteEmpresarial { get; }

    /// <summary>
    /// Usuario y contraseña, cifrados en reposo, de cada Empresa en la Plataforma CAE de un
    /// Cliente empresarial. Proyectar <c>Usuario</c> o <c>Contrasena</c> los descifra: solo
    /// desde Queries marcadas (<c>IConsultaDeDatosDeCredencial</c>, <c>IConsultaDeSecretosDeTenant</c>).
    /// </summary>
    IQueryable<AccesoPlataformaCaeEmpresa> AccesosPlataformaCaeEmpresa { get; }
}
