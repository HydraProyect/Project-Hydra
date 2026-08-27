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

    public async Task<ContrapartesVigentes> ObtenerContrapartesVigentesAsync(
        Guid proveedoraId, CancellationToken cancellationToken = default)
    {
        var contraparteIds = await dbContext.RelacionesEmpresariales
            .Where(r => r.ProveedoraId == proveedoraId && r.VigenciaHasta == null)
            .Select(r => r.ClienteId)
            .ToListAsync(cancellationToken);

        if (contraparteIds.Count == 0)
            return ContrapartesVigentes.Vacias;

        // Segunda consulta, acotada a esos Ids, contra Empresas — que SÍ
        // filtra soft delete por su filtro global. Una contraparte eliminada
        // no vuelve de aquí y cae en Opacas: clasificar por defecto sería
        // exactamente el fallo que este método existe para impedir.
        var clasificadas = await dbContext.Empresas
            .Where(e => contraparteIds.Contains(e.Id))
            .Select(e => new { e.Id, e.EsPropia, EsCliente = e.EsCritico != null })
            .ToListAsync(cancellationToken);

        var clienteIds = clasificadas.Where(x => x.EsCliente).Select(x => x.Id).ToList();
        var empresaPropiaIds = clasificadas.Where(x => x.EsPropia).Select(x => x.Id).ToList();
        var reconocidas = clasificadas.Where(x => x.EsCliente || x.EsPropia).Select(x => x.Id).ToHashSet();
        var opacaIds = contraparteIds.Where(id => !reconocidas.Contains(id)).Distinct().ToList();

        return new ContrapartesVigentes(clienteIds, empresaPropiaIds, opacaIds);
    }

    public async Task<bool> AgregarSiNoVigenteAsync(
        Guid proveedoraId, Guid clienteId, DateTime ahora, Guid? enmarcadaEnId = null,
        CancellationToken cancellationToken = default)
    {
        // Primero el ChangeTracker: un alta anterior de ESTA misma
        // transacción todavía no está en la base de datos y una consulta no
        // la vería — sin esto, el segundo Add del mismo par revienta el
        // índice único parcial en el SaveChanges.
        var yaEnMemoria = dbContext.RelacionesEmpresariales.Local
            .Any(r => r.ProveedoraId == proveedoraId && r.ClienteId == clienteId && r.VigenciaHasta == null);
        if (yaEnMemoria)
            return false;

        var yaVigente = await ObtenerVigentePorParAsync(proveedoraId, clienteId, cancellationToken);
        if (yaVigente is not null)
            return false;

        dbContext.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(proveedoraId, clienteId, ahora, enmarcadaEnId));
        return true;
    }

    public async Task<bool> CerrarVigenteAsync(
        Guid proveedoraId, Guid clienteId, DateTime ahora, CancellationToken cancellationToken = default)
    {
        var vigente = await ObtenerVigentePorParAsync(proveedoraId, clienteId, cancellationToken);
        if (vigente is null)
            return false;

        vigente.Cerrar(ahora);
        return true;
    }

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
