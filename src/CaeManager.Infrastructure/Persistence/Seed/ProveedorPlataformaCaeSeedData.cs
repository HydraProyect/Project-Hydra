namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Catálogo de proveedores de plataforma CAE — semilla verificada por el
/// propietario (PLAN-EJECUCION-UX.md § Parte 2 (a), 2026-08-05). Los Id son
/// fijos y deterministas (mismo criterio que <see cref="TipoDocumentoSeedData"/>)
/// para que la migración sea reproducible en cualquier entorno.
///
/// La resolución matchea por dominio y sufijo (subdominios incluidos, ver
/// <c>IResolucionProveedorPlataformaCaeService</c>); multi-match cuando dos
/// proveedores comparten dominio a propósito (Sabentis/Quirón Prevención).
/// </summary>
public static class ProveedorPlataformaCaeSeedData
{
    public static readonly (Guid Id, string Codigo, string Nombre, string? Grupo, bool Activo, string[] Dominios)[] Datos =
    [
        (new Guid("60000000-0000-0000-0000-000000000001"), "nalanda", "Nalanda", "Once For All", true, ["nalandaglobal.com"]),
        (new Guid("60000000-0000-0000-0000-000000000002"), "dokify", "Dokify", "Once For All", true, ["dokify.net"]),
        (new Guid("60000000-0000-0000-0000-000000000003"), "twind", "Twind", "Twind (CTAIMA Group)", true, ["app.twind.io", "ctaima.com"]),
        (new Guid("60000000-0000-0000-0000-000000000004"), "ctaimacae-legacy", "CTAIMACAE (legacy)", "Twind (CTAIMA Group)", true, ["ctaimacae.net"]),
        (new Guid("60000000-0000-0000-0000-000000000005"), "e-coordina", "e-coordina", "Twind (CTAIMA Group)", true, ["e-coordina.es"]),
        (new Guid("60000000-0000-0000-0000-000000000006"), "metacontratas", "Metacontratas", null, true, ["metacontratas.com"]),
        (new Guid("60000000-0000-0000-0000-000000000007"), "coordinaplus", "CoordinaPlus", "Addingplus", true, ["adding-plus.com", "coordinaplus.net"]),
        (new Guid("60000000-0000-0000-0000-000000000008"), "ucae", "UCAE", null, true, ["ucae.es"]),
        (new Guid("60000000-0000-0000-0000-000000000009"), "validate", "Validate", null, true, ["validate.es", "validate.network"]),
        (new Guid("6000000A-0000-0000-0000-000000000001"), "egestiona", "eGestiona", null, true, ["egestiona.com", "egestiona.es"]),
        (new Guid("6000000A-0000-0000-0000-000000000002"), "smartosh", "SmartOSH", "Prevencontrol", true, ["smartosh.com"]),
        (new Guid("6000000A-0000-0000-0000-000000000003"), "ecogestor", "EcoGestor", "Eurofins", true, ["ecogestor.com"]),
        (new Guid("6000000A-0000-0000-0000-000000000004"), "sabentis", "Sabentis", null, true, ["sabentis.com", "quironprevencion.com"]),
        (new Guid("6000000A-0000-0000-0000-000000000005"), "unifikas", "Unifikas", null, true, ["unifikas.com"]),
        (new Guid("6000000A-0000-0000-0000-000000000006"), "quiron-prevencion", "Quirón Prevención", "Quirónprevención", true, ["quironprevencion.com"]),
        (new Guid("6000000A-0000-0000-0000-000000000007"), "previntegral", "Previntegral", null, true, ["previntegral.com"]),
        (new Guid("6000000A-0000-0000-0000-000000000008"), "norprevencion", "Norprevención", "Vithas", true, ["vithas.es"]),
        (new Guid("6000000A-0000-0000-0000-000000000009"), "ergasia", "Ergasia", null, true, ["ergasia.es"]),
        (new Guid("6000000B-0000-0000-0000-000000000001"), "valora", "Valora", null, true, ["valoraprevencion.es"]),
        (new Guid("6000000B-0000-0000-0000-000000000002"), "playcae", "PlayCAE", null, true, ["playcae.com"]),
        (new Guid("6000000B-0000-0000-0000-000000000003"), "docuprl", "DocuPRL", null, true, []),
        (new Guid("6000000B-0000-0000-0000-000000000004"), "arch", "Arch", null, false, ["archbus.com"]),
        (new Guid("6000000B-0000-0000-0000-000000000005"), "opground", "Opground", null, false, ["opground.com"]),
    ];
}
