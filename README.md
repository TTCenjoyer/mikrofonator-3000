# Filtr Mikrofonu

Aplikacja dla Windows x64 przetwarza mikrofon na żywo. Wybierz urządzenie wejściowe i wyjście, a potem kliknij **Uruchom mikrofon**. Filtr, jego intensywność i oba wzmocnienia możesz zmieniać w trakcie rozmowy. Odsłuch uruchomisz osobno na słuchawkach.

## Discord i inne komunikatory

1. W aplikacji wybierz fizyczny mikrofon oraz **CABLE Input (VB-Audio Virtual Cable)** jako wyjście.
2. W Discordzie otwórz **Ustawienia użytkownika → Głos i wideo**. Jako urządzenie wejściowe wybierz **CABLE Output (VB-Audio Virtual Cable)**. Jako wyjście zostaw swoje słuchawki lub głośniki.
3. Kliknij **Uruchom mikrofon** i użyj testu mikrofonu w Discordzie. Jeśli Discord tłumi mocno przetworzone efekty, wyłącz jego redukcję szumów. [Instrukcja testu mikrofonu](https://support.discord.com/hc/en-us/articles/360020641332-Mic-Testing) · [VB-CABLE](https://vb-audio.com/Cable/)

Przetwarzanie działa lokalnie. Program zapamiętuje wybór urządzeń i ustawienia filtrów. Aplikacja otwiera się z filtrem **Czysty**. Preverb, Reverb i Echo włączasz w panelu efektów, a mikrofon uruchamiasz przyciskiem. Odsłuch również włączasz ręcznie.

## Preverb, reverb i echo na żywo

Włącz odsłuch w słuchawkach, żeby stroić efekty podczas mówienia. Wszystkie suwaki działają także w trakcie przesyłania dźwięku do komunikatora.

- **Preverb** włącza pogłos przed głosem. Regulujesz czas narastania od 0,4 do 2,4 s oraz głośność pogłosu. Wypowiedź jest opóźniona o ustawiony czas.
- **Reverb** ma dziewięć suwaków ze zrzutu Audacity oraz Wet Only. Room Size określa rozmiar pomieszczenia, Pre-delay opóźnia pogłos, Reverberance wydłuża wybrzmiewanie, a Damping tłumi wysokie częstotliwości odbić. Tone Low i Tone High zmieniają barwę. Wet Gain oraz Dry Gain sterują poziomami pogłosu i głosu. Stereo Width reguluje szerokość pogłosu w odsłuchu i na wyjściu stereo. Wet Only pozostawia sam pogłos. Silnik pogłosu aplikacji daje własne brzmienie przy tych samych nazwach parametrów.
- **Echo** dodaje echo wypowiedzi. Ustawiasz głośność oraz odstęp między powtórzeniami.

Efekty można łączyć. Program zapamiętuje każde ustawienie.

Preverb ma osobny zestaw tych samych dziewięciu parametrów i Wet Only. Tworzy odwrócony, narastający pogłos pomieszczenia, ograniczony do wybranego czasu przed głosem. Jego Pre-delay ustawia przerwę między pogłosem a głosem. Parametry są przygotowywane w tle i płynnie wprowadzane do działającego strumienia. Preverb oraz Reverb zapamiętują własne ustawienia.

Echo to oddzielne powtórzenia wypowiedzi. Reverb daje gęste odbicia pomieszczenia, które zlewają się w pogłos.

## Presety z nagrań

- **Z próbki** odtwarza w przybliżeniu efekt zmierzony na dwóch przebiegach testowych z `untitled.wav`. Łączy korekcję pasma z nasyceniem i filtrem górnoprzepustowym 1 kHz.
- **Preverb** odtwarza narastający pogłos na podstawie pierwszej części `12.wav`. Domyślny czas to 1,3 s. Do opóźnienia dochodzi zwykła latencja urządzeń audio.

Do działania wystarcza EXE.

## Kompilacja

Użyj .NET 10 SDK na Windows. Polecenie w katalogu projektu tworzy przenośny EXE dla Windows x64.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Obsługa urządzeń audio korzysta z [NAudio](https://github.com/naudio/NAudio). Wirtualne wejście zapewnia zainstalowany sterownik VB-CABLE.


## Bramka i redukcja szumów

Bramka i redukcja szumów działają przed filtrami głosu, Preverb, Reverb i Echo. Próg bramki ustaw powyżej poziomu szumu w ciszy. Podtrzymanie i wygaszanie pomagają zachować końcówki słów. Redukcja szumów ma osobny poziom szumu oraz siłę działania. Wszystkie wartości zmieniasz podczas rozmowy.

## Własny wirtualny mikrofon

W katalogu Driver znajduje się prototyp źródeł sterownika na bazie Microsoft SysVAD. Wersja 1.8 wykrywa jego docelowe urządzenia Filtr Mikrofonu Input i Filtr Mikrofonu Microphone. Sterownik wymaga kompilacji z WDK, testów oraz podpisu akceptowanego przez Windows przed instalacją. Ten pakiet EXE obsługuje obecne wyjścia audio, w tym VB-CABLE, do czasu ukończenia sterownika.


### Presety i filtry (1.9)
Dotychczasowe barwy głosu są dostępne jako **Presety**. Lewy panel **Filtry** działa niezależnie od wybranego presetu. Ustawienia filtrów zapisują się przy zamknięciu aplikacji.

- **Korektor EQ** — osiem punktów 63 Hz–16 kHz, zakres ±18 dB, proste odcinki na logarytmicznej osi częstotliwości. Przeciąganie zmienia punkt, dwuklik zeruje pasmo. Po zaznaczeniu wykresu strzałki lewo/prawo wybierają pasmo, góra/dół zmieniają je o 0,5 dB. Przycisk zeruje całą krzywą. Symetryczny filtr FIR zachowuje liniową fazę; tor ma stałe dodatkowe opóźnienie 512 próbek (10,7 ms przy 48 kHz), także przy wyłączonym EQ, aby przełączanie nie zmieniało synchronizacji.
- **Filtr górnoprzepustowy** — odcięcie 20–3000 Hz, nachylenie 12/24/48 dB na oktawę.
- **Przester (distortion)** — siła, domieszka, poziom wyjściowy i twarde lub miękkie obcinanie.
- **Normalizator** — automatyczna regulacja poziomu na żywo z docelowym poziomem szczytowym i ograniczeniem maksymalnego podbicia. To adaptacja do streamingu; nie analizuje całego nagrania z wyprzedzeniem jak efekt offline.

Kolejność: redukcja i bramka szumów → filtr górnoprzepustowy → EQ → przester → preset → normalizator → preverb → reverb i echo. Nowe filtry są domyślnie wyłączone i mają niezależne włączniki.


### Układ i wspólne presety (1.10)
Urządzenia znajdują się po lewej, presety i krótki pasek mierników pośrodku, a biblioteka filtrów po prawej. Ustawienia filtrów otwierają osobne, niemodalne okna, więc można je regulować podczas odsłuchu. Przełącznik w oknie i w bibliotece steruje tym samym filtrem. Wskaźniki przechodzą z zieleni przez żółć do czerwieni przy wysokim poziomie.

Presety ustawiają dostępne filtry, bez równoległego ukrytego efektu presetu. Telefon i Megafon ustawiają EQ, filtr górnoprzepustowy i przester. Studio korzysta z korekcji, filtra górnoprzepustowego, kompresora i normalizatora. Alastor włącza filtr górnoprzepustowy i barwę radiową z próbki, a Anonim dodatkowo korzysta ze zmiany wysokości głosu. Barwa radiowa, kompresor i zmiana głosu są też dostępne osobno, z regulacją intensywności. Ręczna edycja filtrów usuwa zaznaczenie presetu i pokazuje własne ustawienia. Czysty wyłącza filtry z prawej biblioteki; osobne ustawienia szumów i przestrzeni pozostają niezależne.
### Alastor ze zrzutów Audacity (1.11)

Preset Alastor korzysta teraz z EQ, Medium Overdrive (Distortion amount 49, Output level 50, domieszka 100%) i filtra górnoprzepustowego 1000 Hz / 6 dB na oktawę. Nie włącza dodatkowej Barwy radiowej.

Krzywa EQ jest przybliżeniem odczytanym ze zrzutu: zawiera dodatkowe punkty i podbicia około 2 i 2,8 kHz. Edytor ma zakres ±30 dB. Skończony filtr FIR wygładza ostre wierzchołki; nie jest to numeryczny import krzywej Audacity. Przester i high-pass mają wartości liczbowe ze zrzutów.

Medium Overdrive używa znormalizowanej symetrycznej krzywej wykładniczej z liniowym poziomem wyjścia. Źródła charakterystyki: [Audacity Manual](https://manual.audacityteam.org/man/distortion.html), [DistortionBase](https://doxy.audacityteam.org/_distortion_base_8cpp_source.html). HP 6 dB jest filtrem pierwszego rzędu.

### Swobodna edycja EQ (1.12)

Kliknięcie pustego miejsca na wykresie dodaje punkt, przeciąganie zmienia zarówno częstotliwość, jak i poziom. Punkty można przesuwać przez sąsiadów; krzywa zachowuje kolejność częstotliwości. Prawy klik albo Delete usuwa punkt (pozostają co najmniej dwa). Dostępne są pola dokładnych wartości 20–20 000 Hz i ±30 dB oraz przyciski dodawania, edycji i wyboru punktu.

Zerowanie zachowuje częstotliwości punktów. Odwracanie zmienia znak wszystkich wzmocnień. Cofnij/Ponów oraz Ctrl+Z/Y obsługują całe gesty przeciągania. Strzałki zmieniają częstotliwość i poziom, Shift zwiększa precyzję, a Page Up/Down wybierają poprzedni/następny punkt. Limit krzywej wynosi 256 punktów.

Skalę częstotliwości można przełączyć między logarytmiczną i liniową, a siatkę ukryć. Przełączenie skali zmienia widok, zachowując tę samą charakterystykę dźwięku. Edycja działa na żywo i zapisuje się razem z ustawieniami aplikacji.

