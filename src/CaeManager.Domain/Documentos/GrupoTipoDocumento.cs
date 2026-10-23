namespace CaeManager.Domain.Documentos;

/// <summary>
/// Grupo de un <see cref="TipoDocumento"/> para la interfaz: el selector de tipo, la subida global, el filtro
/// «Grupo» del listado y la pestaña Documentación de las fichas 360 agrupan por él (catálogo aprobado el
/// 2026-10-17). Son quince grupos para los tipos de Trabajador y de Empresa. Los tipos de Vehículo y de
/// Proyecto no llevan grupo: se agrupan por su ámbito (<see cref="TipoDocumento.AmbitoAplicacion"/>), igual
/// que un tipo creado por un Tenant que todavía no tiene grupo.
///
/// <para>
/// Se persiste como cadena, igual que el resto de enums de la casa, así que renombrar un valor se ve en la base
/// en lugar de reinterpretar filas. Los ordinales no viajan por la API, pero se fijan para que el orden de los
/// grupos en pantalla no dependa de dónde se inserte un valor nuevo.
/// </para>
/// </summary>
public enum GrupoTipoDocumento
{
    IdentidadYExtranjeria = 1,
    VigilanciaDeLaSalud = 2,
    RelacionLaboral = 3,
    InformacionYEpi = 4,
    FormacionPrl = 5,
    HabilitacionesYOficios = 6,
    SeguridadSocialYHacienda = 7,
    SegurosYMutua = 8,
    OrganizacionPreventiva = 9,
    CoordinacionCae = 10,
    SocietariaYAdministrativa = 11,
    Certificaciones = 12,
    EmpresaExtranjera = 13,
    Igualdad = 14,
    Construccion = 15
}

/// <summary>Rótulos en pantalla de <see cref="GrupoTipoDocumento"/>, tal como los aprobó el catálogo.</summary>
public static class GruposTipoDocumento
{
    public static string Rotulo(GrupoTipoDocumento grupo) => grupo switch
    {
        GrupoTipoDocumento.IdentidadYExtranjeria => "Identidad y extranjería",
        GrupoTipoDocumento.VigilanciaDeLaSalud => "Vigilancia de la salud",
        GrupoTipoDocumento.RelacionLaboral => "Relación laboral",
        GrupoTipoDocumento.InformacionYEpi => "Información y EPI",
        GrupoTipoDocumento.FormacionPrl => "Formación PRL",
        GrupoTipoDocumento.HabilitacionesYOficios => "Habilitaciones y oficios",
        GrupoTipoDocumento.SeguridadSocialYHacienda => "Seguridad Social y Hacienda",
        GrupoTipoDocumento.SegurosYMutua => "Seguros y mutua",
        GrupoTipoDocumento.OrganizacionPreventiva => "Organización preventiva",
        GrupoTipoDocumento.CoordinacionCae => "Coordinación CAE",
        GrupoTipoDocumento.SocietariaYAdministrativa => "Societaria y administrativa",
        GrupoTipoDocumento.Certificaciones => "Certificaciones",
        GrupoTipoDocumento.EmpresaExtranjera => "Empresa extranjera",
        GrupoTipoDocumento.Igualdad => "Igualdad",
        GrupoTipoDocumento.Construccion => "Construcción",
        _ => grupo.ToString()
    };

    /// <summary>
    /// Rótulo del grupo con el que se agrupa un tipo en pantalla: su grupo si lo tiene; si no, su ámbito
    /// (Vehículo, Obra / Proyecto) o «Sin grupo» para un tipo de Trabajador o Empresa que no lo tiene asignado.
    /// </summary>
    public static string RotuloVisible(GrupoTipoDocumento? grupo, AmbitoAplicacion ambito) => grupo is { } g
        ? Rotulo(g)
        : ambito switch
        {
            AmbitoAplicacion.Vehiculo => "Vehículo",
            AmbitoAplicacion.Proyecto => "Obra / Proyecto",
            _ => "Sin grupo"
        };
}
