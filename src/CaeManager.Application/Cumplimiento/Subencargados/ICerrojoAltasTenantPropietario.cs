namespace CaeManager.Application.Cumplimiento.Subencargados;

/// <summary>
/// Cierra las altas de Tenant propietario hasta que termine la transacción en curso (#2010, D-7, revisión Codex P2 de
/// la PR #2274). Lo toma <see cref="ActivarSubencargadoCommandHandler"/> antes de releer los Tenants propietarios y lo
/// suelta al confirmar <c>Activo</c>: un Tenant cuya alta se confirmó antes aparece en esa relectura, y uno cuya alta
/// empieza después espera a que el subencargado ya conste activo.
/// </summary>
/// <remarks>
/// La barrera la pone PostgreSQL y no cada alta: es <c>LOCK TABLE "Tenants" IN SHARE MODE</c>, que choca con el
/// <c>ROW EXCLUSIVE</c> que toma cualquier <c>INSERT</c> en esa tabla, venga de un Command, de una siembra o de SQL
/// directo, y espera a que se confirmen las altas que ya insertaron. No impide leer Tenants; sí retiene, lo que dure
/// la activación, cualquier escritura en esa tabla. Exige una transacción en curso: fuera de ella no retendría nada.
/// </remarks>
public interface ICerrojoAltasTenantPropietario
{
    Task CerrarAsync(CancellationToken cancellationToken = default);
}
