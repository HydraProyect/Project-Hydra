namespace CaeManager.Domain.RelacionesEmpresariales;

public interface IRelacionEmpresarialRepository
{
    void Agregar(RelacionEmpresarial relacion);

    /// <summary>
    /// La relación vigente (si existe) para el par proveedora×cliente — la
    /// que un "Editar" debe cerrar antes de que se pueda abrir una nueva
    /// para el mismo par (nunca se edita in situ). Sin <c>tenantId</c>
    /// explícito a propósito: el filtro global de EF ya acota la consulta al
    /// tenant activo, mismo patrón que el resto de repositorios (ver
    /// <c>IEmpresaRepository</c>) — pasarlo aparte sería redundante y
    /// rompería la convención.
    /// </summary>
    Task<RelacionEmpresarial?> ObtenerVigentePorParAsync(
        Guid proveedoraId, Guid clienteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// El candidato único a <c>EnmarcadaEnId</c> para una nueva relación
    /// Subcontrata→Cliente, dado un conjunto EXPLÍCITO de Empresas candidatas
    /// (las que la Subcontrata tiene o va a tener vinculadas — en memoria,
    /// no necesariamente ya persistidas: al crear una Subcontrata nueva sus
    /// propios vínculos con Empresas todavía no existen en la base de
    /// datos). Devuelve la relación vigente de primer nivel (Empresa
    /// propia→Cliente) tal que la Empresa esté en <paramref name="empresaIdsCandidatas"/>.
    /// Null si no hay ningún candidato o si hay más de uno — nunca se elige
    /// a ciegas entre varios (ADR-011 § 15). Mismo criterio verificado en la
    /// revisión adversaria de F4 (17/33 deterministas, 16/33 sin resolución
    /// automática).
    /// </summary>
    Task<Guid?> ObtenerCandidatoUnicoParaEnmarcarAsync(
        IReadOnlyCollection<Guid> empresaIdsCandidatas, Guid clienteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True si <paramref name="propuestaEnmarcadaEnId"/> ya está enmarcada,
    /// directa o transitivamente, en <paramref name="relacionId"/> — es
    /// decir, si enmarcar <paramref name="relacionId"/> en
    /// <paramref name="propuestaEnmarcadaEnId"/> cerraría un ciclo.
    /// Obligatorio antes de fijar <c>EnmarcadaEnId</c> en cualquier alta o
    /// reencuadre: el esquema físico, por sí solo, ACEPTA un ciclo de 2+
    /// pasos (demostrado experimentalmente, ver diseño físico § 2.2 y § 8ter)
    /// — esta es la única garantía real.
    /// </summary>
    Task<bool> CreariaUnCicloAsync(
        Guid relacionId, Guid propuestaEnmarcadaEnId, CancellationToken cancellationToken = default);
}
