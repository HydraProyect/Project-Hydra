using Xunit;

// Cada colección de test (AppCollection/AppCollectionMultiTenant/
// AppCollectionRetencion/AppCollectionSoporte) arranca su propia app real +
// migra + siembra ~2000 filas en su propio Postgres, y xUnit corre las
// colecciones EN PARALELO por defecto — con 4 colecciones a la vez en un
// runner de 2 núcleos (CI), la contención de CPU real ralentiza lo
// suficiente como para que tests sin relación entre sí (AlcanceRolesTests)
// superen sus timeouts fijos de Playwright (15-30s), visto en CI: no un
// fallo del propio test, sino del runner sirviendo cuatro apps a la vez.
//
// Desactivar el paralelismo entre colecciones cambia velocidad por
// fiabilidad — el tiempo total del job ya se mide en minutos por el propio
// arranque de la app real, así que unos minutos más corriendo en serie es
// mejor cambio que un test rojo por contención una vez sí y otra no.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
