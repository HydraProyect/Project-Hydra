using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.CatalogoBase;

namespace CaeManager.Domain.Inbound.Requisitos;

/// <summary>
/// Plantilla de requisitos del Tenant TCE (CA-1; plan backend Inbound § 2.3): lo que pide a quien entra por una Actividad
/// o un riesgo del catálogo base, por un Centro propio o como exigencia de un Cliente empresarial propio (N3, D-15). Las
/// capas se resuelven al leer (B-15); la plantilla no se copia al contrato de acceso.
///
/// <para>
/// <b>Una referencia por plantilla</b>, la de su <see cref="Tipo"/>, y una sola plantilla activa por referencia (índices
/// únicos parciales). Se retira, no se borra ni se reactiva: para volver a pedir algo se crea otra, y lo ya pedido con la
/// retirada sigue citándola.
/// </para>
///
/// <para>
/// <b>Requisitos.</b> Solo se añaden, editan y retiran por los métodos de la plantilla, que renuevan su versión: la fila
/// del requisito vive en otra tabla y, sin tocar la raíz, dos ediciones simultáneas no se verían.
/// </para>
/// </summary>
public sealed class PlantillaRequisitos : EntidadBase
{
    private readonly List<RequisitoPlantilla> _requisitos = [];

    public TipoPlantillaRequisitos Tipo { get; private set; }

    public Guid? ActividadId { get; private set; }

    public Guid? RiesgoId { get; private set; }

    public Guid? CentroId { get; private set; }

    /// <summary>Empresa del Tenant TCE que es su Cliente empresarial, en una plantilla de exigencias.</summary>
    public Guid? ClienteEmpresarialId { get; private set; }

    public bool Activa { get; private set; } = true;

    public IReadOnlyCollection<RequisitoPlantilla> Requisitos => _requisitos.AsReadOnly();

    private PlantillaRequisitos()
    {
    }

    private PlantillaRequisitos(TipoPlantillaRequisitos tipo) => Tipo = tipo;

    public static PlantillaRequisitos ParaActividad(ActividadCatalogo actividad)
    {
        ArgumentNullException.ThrowIfNull(actividad);
        if (!actividad.Activa)
            throw new InvalidOperationException("Una Actividad retirada del catálogo base no recibe plantilla nueva.");
        return new PlantillaRequisitos(TipoPlantillaRequisitos.PorActividad) { ActividadId = actividad.Id };
    }

    public static PlantillaRequisitos ParaRiesgo(RiesgoCatalogo riesgo)
    {
        ArgumentNullException.ThrowIfNull(riesgo);
        if (!riesgo.Activo)
            throw new InvalidOperationException("Un riesgo retirado del catálogo base no recibe plantilla nueva.");
        return new PlantillaRequisitos(TipoPlantillaRequisitos.PorRiesgo) { RiesgoId = riesgo.Id };
    }

    /// <summary>El Centro tiene que ser del Tenant TCE; lo garantizan la FK compuesta y el filtro de Tenant.</summary>
    public static PlantillaRequisitos ParaCentro(Guid centroId)
    {
        if (centroId == Guid.Empty)
            throw new ArgumentException("El Centro es obligatorio.", nameof(centroId));
        return new PlantillaRequisitos(TipoPlantillaRequisitos.DeCentro) { CentroId = centroId };
    }

    /// <summary>
    /// Exigencias de un Cliente empresarial del Tenant TCE (D-15). Que la Empresa lo sea lo comprueba quien llama; la FK
    /// compuesta garantiza que es del mismo Tenant.
    /// </summary>
    public static PlantillaRequisitos ParaExigenciasDe(Guid clienteEmpresarialId)
    {
        if (clienteEmpresarialId == Guid.Empty)
            throw new ArgumentException("El Cliente empresarial es obligatorio.", nameof(clienteEmpresarialId));
        return new PlantillaRequisitos(TipoPlantillaRequisitos.ExigenciasClienteEmpresarial) { ClienteEmpresarialId = clienteEmpresarialId };
    }

    /// <summary>
    /// Añade un requisito. Un tipo de documento se pide una sola vez por plantilla mientras su requisito esté activo.
    /// </summary>
    public RequisitoPlantilla AnadirRequisito(TipoDocumento tipo, RequisitoBase? requisitoBase, AtributosRequisito atributos)
    {
        ArgumentNullException.ThrowIfNull(tipo);
        ExigirActiva();
        if (_requisitos.Any(r => r.Activo && r.TipoDocumentoId == tipo.Id))
            throw new InvalidOperationException("Ese tipo de documento ya se pide en esta plantilla.");

        var requisito = new RequisitoPlantilla(this, tipo, requisitoBase, atributos);
        _requisitos.Add(requisito);
        RenovarVersion();
        return requisito;
    }

    public void EditarRequisito(Guid requisitoId, TipoDocumento tipo, RequisitoBase? requisitoBase, AtributosRequisito atributos)
    {
        ArgumentNullException.ThrowIfNull(tipo);
        ExigirActiva();
        Requisito(requisitoId).Editar(this, tipo, requisitoBase, atributos);
        RenovarVersion();
    }

    public void RetirarRequisito(Guid requisitoId)
    {
        ExigirActiva();
        var requisito = Requisito(requisitoId);
        if (!requisito.Activo)
            return;
        requisito.Retirar();
        RenovarVersion();
    }

    public void Retirar()
    {
        if (!Activa)
            return;
        Activa = false;
        RenovarVersion();
    }

    private RequisitoPlantilla Requisito(Guid requisitoId) =>
        _requisitos.FirstOrDefault(r => r.Id == requisitoId)
        ?? throw new InvalidOperationException("El requisito no es de esta plantilla.");

    private void ExigirActiva()
    {
        if (!Activa)
            throw new InvalidOperationException("Una plantilla retirada no cambia: se crea otra.");
    }
}
