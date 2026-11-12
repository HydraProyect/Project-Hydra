using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Para las pruebas que construyen a mano un escritor de Empresas y prueban otra propiedad (duplicados, relaciones,
/// importación de varias filas, restauración): el límite de una sola Empresa propia de D-P11 se prueba en
/// <c>LimiteEmpresaPropiaTests</c> (Application) y aquí no debe tapar la propiedad que cada prueba comprueba.
/// </summary>
internal sealed class LimiteEmpresaPropiaSinAplicar : ILimiteEmpresaPropia
{
    public Task<Error?> ComprobarAltaAsync(int propiasAnadidasSinGuardar, bool esIntento, CancellationToken cancellationToken = default) =>
        Task.FromResult<Error?>(null);
}