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

    public Task<Error?> ComprobarGuardadoAsync(int propiasSinGuardar, CancellationToken cancellationToken = default) =>
        Task.FromResult<Error?>(null);

    // Sin candado: estas pruebas no compiten por el límite, y su transacción, si la hay, es la del propio escritor.
    public Task<Result> SerializarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default) =>
        operacion(cancellationToken);
}