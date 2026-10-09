namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Un botón de <see cref="FranjaEstado"/>. <c>Texto</c> es lo que lee el gestor (en plural: «Vencidos»),
/// <c>Tono</c> el color del punto y <c>Valores</c> los estados de código que el botón marca y desmarca a la vez:
/// lo normal es uno, pero «Por vencer» agrupa Urgente y Próximo, que la interfaz rotula igual y el código
/// sigue distinguiendo. Son los mismos valores que viajan a la URL y a la Query.
/// </summary>
public sealed record OpcionFranjaEstado(string Texto, TonoBadge Tono, params string[] Valores);
