# Filtr Mikrofonu — prototyp sterownika

Stan pakietu jest rozwojowy. Źródła nie zostały skompilowane, przetestowane w jądrze Windows ani podpisane. Pakiet nie zawiera gotowego SYS ani instalatora.

Kod Microsoft SysVAD jest dostępny w Source/audio/sysvad i zachowuje licencję z Source/LICENSE. Commit źródłowy zapisano w UPSTREAM.txt.

## Wprowadzone zmiany

- Dwa urządzenia na podstawie endpointów Speaker i MicIn, z nazwami Filtr Mikrofonu Input oraz Filtr Mikrofonu Microphone.
- Ograniczony bufor PCM w pamięci nie stronicowanej zamiast testowego generatora tonu i zapisu do WAV.
- Format transportu 48 kHz PCM16. Wejście od aplikacji jest stereo, a wyjście mikrofonowe mono.
- Współbieżne odczyty mają własne kursory, a zapis ma jednego właściciela. Przepełniony odczyt wraca w pobliże bieżącego zapisu.
- INF używa identyfikatora Root\FiltrMikrofonu.

## Dokończenie pakietu

Potrzebne są Visual Studio z narzędziami C++, zgodne Windows SDK i WDK oraz MSBuild. Kompilację projektu Source/audio/sysvad/TabletAudioSample/TabletAudioSample.vcxproj należy przeprowadzić w konfiguracji Release x64. Zależny projekt EndpointsCommon również wymaga WDK.

Przed przygotowaniem instalacji należy zweryfikować negocjację formatu obu endpointów w Windows Audio Engine, pakiet INF, cykle stop/start, wiele klientów przechwytujących, odłączenia urządzeń, brak zapisu starych próbek oraz testy Driver Verifier/HLK. Globalny transport prototypu zakłada pojedynczą instancję urządzenia i nie implementuje miksowania.

Produkcyjny pakiet wymaga podpisu Microsoft uzyskanego w Windows Hardware Developer Program. Materiały do przygotowania zgłoszenia wymagają uprawnień właściciela konta. Brak podpisu uniemożliwia ukończenie zwykłej instalacji na tym komputerze.

Źródła:
- https://github.com/microsoft/Windows-driver-samples/tree/main/audio/sysvad
- https://learn.microsoft.com/windows-hardware/drivers/dashboard/code-signing-attestation
