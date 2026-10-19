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
/// Resuelve la Delegación y el Oficio contra el catálogo de la Empresa del Trabajador y los fija, en dos pasos para que
/// quien llama valide antes de tocar nada: <see cref="ResolverAsync"/> solo lee y devuelve el error, y
/// <see cref="Aplicar"/> muta. Así un comando que también cambia otros campos del Trabajador no deja la entidad
/// rastreada a medio cambiar cuando el catálogo no es válido (el DbContext del circuito la guardaría después).
/// Quien llama ya ha comprobado que el Trabajador es visible, que es de una Empresa y que su actor gestiona esa Empresa;
/// aquí solo se comprueba que los dos Ids son del catálogo de esa misma Empresa (un Id ajeno responde «no encontrada»;
/// por debajo, la FK compuesta lo rechazaría igual).
/// </summary>
internal static class AsignacionDelegacionEmpresaYOficio
{
    public sealed record Resuelta(DelegacionEmpresa? DelegacionEmpresa, OficioEmpresa? Oficio);

    public static async Task<(Resuelta? Asignacion, Error? Error)> ResolverAsync(
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
                return (null, Error.Crear("Delegacion.NoEncontrada", "No encontramos esta delegación en su empresa."));
        }

        OficioEmpresa? oficio = null;
        if (oficioId is { } idOficio)
        {
            oficio = await catalogo.ObtenerOficioAsync(idOficio, cancellationToken);
            if (oficio is null || oficio.EmpresaId != empresaId)
                return (null, Error.Crear("Oficio.NoEncontrado", "No encontramos este oficio en su empresa."));
        }

        if (oficio is not null && trabajador.EstaAnonimizado)
            return (null, Error.Crear("Trabajador.Anonimizado",
                "Este trabajador está anonimizado: sus datos personales se suprimieron y no pueden volver a introducirse."));

        return (new Resuelta(delegacion, oficio), null);
    }

    /// <summary>
    /// Fija lo resuelto. Con <paramref name="renovarVersionDelCatalogo"/>, la Delegación o el Oficio que pasa a estar
    /// asignado renueva su versión (<c>RegistrarAsignacion</c>): si alguien lo elimina a la vez, uno de los dos guardados
    /// choca por concurrencia. El alta no lo hace (decisión de la coordinación, 2026-10-19): <c>CrearTrabajadorCommand</c>
    /// puede correr como <c>cae_app_aprovisionamiento</c>, que solo lee el catálogo, y renovar la versión sería un UPDATE
    /// que falla con 42501. La carrera entre un alta y una eliminación simultáneas queda abierta a propósito.
    /// </summary>
    public static void Aplicar(Trabajador trabajador, Resuelta asignacion, bool renovarVersionDelCatalogo)
    {
        if (renovarVersionDelCatalogo)
        {
            if (asignacion.DelegacionEmpresa is { } delegacion && trabajador.DelegacionEmpresaId != delegacion.Id)
                delegacion.RegistrarAsignacion();
            if (asignacion.Oficio is { } oficio && trabajador.OficioEmpresaId != oficio.Id)
                oficio.RegistrarAsignacion();
        }

        trabajador.FijarDelegacionEmpresaYOficio(asignacion.DelegacionEmpresa, asignacion.Oficio);
    }
}
