using CaeManager.Domain.RelacionesEmpresariales;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class RelacionEmpresarialRepository(CaeManagerDbContext dbContext) : IRelacionEmpresarialRepository
{
    /// <summary>
    /// Tope defensivo de recorrido de la cadena — el producto capa la
    /// profundidad real a tres niveles (ADR-011 § 2.4); una cadena más larga
    /// que esto es anómala y se trata como ciclo (fallo cerrado), no como
    /// falso negativo por agotar el bucle.
    /// </summary>
    private const int LimiteProfundidadCadena = 50;

    public void Agregar(RelacionEmpresarial relacion) => dbContext.RelacionesEmpresariales.Add(relacion);

    public async Task<RelacionEmpresarial?> ObtenerVigentePorParAsync(
        Guid proveedoraId, Guid clienteId, CancellationToken cancellationToken = default) =>
        await dbContext.RelacionesEmpresariales.FirstOrDefaultAsync(
            r => r.ProveedoraId == proveedoraId && r.ClienteId == clienteId && r.VigenciaHasta == null,
            cancellationToken);

    public async Task<Guid?> ObtenerCandidatoUnicoParaEnmarcarAsync(
        IReadOnlyCollection<Guid> empresaIdsCandidatas, Guid clienteId, CancellationToken cancellationToken = default)
    {
        if (empresaIdsCandidatas.Count == 0)
            return null;

        var candidatos = await dbContext.RelacionesEmpresariales
            .Where(r => empresaIdsCandidatas.Contains(r.ProveedoraId) && r.ClienteId == clienteId && r.VigenciaHasta == null)
            .Select(r => r.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        return candidatos.Count == 1 ? candidatos[0] : null;
    }

    public async Task<bool> CreariaUnCicloAsync(
        Guid relacionId, Guid propuestaEnmarcadaEnId, CancellationToken cancellationToken = default)
    {
        var actual = propuestaEnmarcadaEnId;

        for (var pasos = 0; pasos < LimiteProfundidadCadena; pasos++)
        {
            if (actual == relacionId)
                return true;

            var siguiente = await dbContext.RelacionesEmpresariales
                .Where(r => r.Id == actual)
                .Select(r => r.EnmarcadaEnId)
                .FirstOrDefaultAsync(cancellationToken);

            if (siguiente is null)
                return false;

            actual = siguiente.Value;
        }

        return true;
    }
}
