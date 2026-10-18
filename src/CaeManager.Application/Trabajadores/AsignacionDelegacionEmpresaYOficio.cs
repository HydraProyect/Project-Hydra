using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;

namespace CaeManager.Application.Trabajadores;

/// <summary>
/// La Delegación y el Oficio que la ficha del Trabajador fija junto con el resto de sus datos (alta y edición de la
/// ficha). En <c>EditarFichaTrabajadorCommand</c>, <c>null</c> (el objeto entero) = no se cambian; dentro, un Id
/// <c>null</c> = sin Delegación o sin Oficio.
/// </summary>
public record DelegacionEmpresaYOficioDeTrabajador(Guid? DelegacionEmpresaId, Guid? OficioEmpresaId);

/// <summary>
/// Resuelve la Delegación y el Oficio contra el catálogo de la Empresa del Trabajador y los fija. Quien llama ya ha
/// comprobado que el Trabajador es visible, que es de una Empresa y que su actor gestiona esa Empresa; aquí solo se
/// comprueba que los dos Ids son del catálogo de esa misma Empresa (un Id ajeno responde «no encontrada»; por debajo,
/// la FK compuesta lo rechazaría igual).
/// </summary>
internal static class AsignacionDelegacionEmpresaYOficio
{
    public static async Task<Error?> FijarAsync(
        Trabajador trabajador, Guid? delegacionId, Guid? oficioId,
        ICatalogoTrabajadoresEmpresaRepository catalogo, CancellationToken cancellationToken)
    {
        var empresaId = trabajador.EmpresaId
            ?? throw new InvalidOperationException("Solo un trabajador de empresa tiene delegación y oficio.");

        DelegacionEmpresa? delegacion = null;
        if (delegacionId is { } idDelegacionEmpresa)
        {
            delegacion = await catalogo.ObtenerDelegacionEmpresaAsync(idDelegacionEmpresa, cancellationToken);
            if (delegacion is null || delegacion.EmpresaId != empresaId)
                return Error.Crear("Delegacion.NoEncontrada", "No encontramos esta delegación en su empresa.");
        }

        OficioEmpresa? oficio = null;
        if (oficioId is { } idOficio)
        {
            oficio = await catalogo.ObtenerOficioAsync(idOficio, cancellationToken);
            if (oficio is null || oficio.EmpresaId != empresaId)
                return Error.Crear("Oficio.NoEncontrado", "No encontramos este oficio en su empresa.");
        }

        if (oficio is not null && trabajador.EstaAnonimizado)
            return Error.Crear("Trabajador.Anonimizado",
                "Este trabajador está anonimizado: sus datos personales se suprimieron y no pueden volver a introducirse.");

        trabajador.FijarDelegacionEmpresaYOficio(delegacion, oficio);
        return null;
    }
}