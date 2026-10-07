namespace LazyDad.Api.Services;

/// <summary>
/// The privacy page's text (<c>/privacy</c>), in Ukrainian and English. GDPR needs it once a voter key exists, and the
/// providers' registrations link to it. Keep it true to the code: what's stored (<c>VoterKeys</c>, <c>Votes</c>), the
/// cookies (<c>SignInSetup</c>) and the services. It isn't legal advice.
/// </summary>
internal static class PrivacyPolicy
{
    public const string Description = "Як LazyDad поводиться з вашими даними: що зберігається, навіщо, як довго і як це видалити.";

    private const string Updated = "2026-10-08";
    private const string Repository = "https://github.com/mykolad/lazydad";
    private const string Owner = "https://github.com/mykolad";

    public static string Html(string backIcon) => $$"""
        <article class="ld-doc" id="ld-privacy">
          <a class="ld-back" href="/" data-home>{{backIcon}}<span data-i18n="back">Усі жарти</span></a>
          <div class="ld-doc-body" lang="uk">
            <h2>Конфіденційність</h2>
            <p class="ld-doc-meta">Оновлено <time datetime="{{Updated}}">8 жовтня 2026</time></p>
            <p>Коротко: LazyDad зберігає якнайменше. Щоб рахувати один голос на жарт від кожного акаунта, йому досить
              коду, обчисленого з акаунта: ні імені, ні пошти, ні фото. Жодної аналітики чи реклами.</p>

            <h3>Хто веде сайт</h3>
            <p>LazyDad (lazydad.fyi) — хобі-проєкт приватної особи, <a href="{{Owner}}">mykolad</a>. Зв’язатися можна
              через GitHub: <a href="{{Repository}}/issues">створіть issue</a> або напишіть через
              <a href="{{Owner}}">профіль mykolad</a>.</p>

            <h3>Що зберігається і навіщо</h3>
            <ul>
              <li><strong>Без входу</strong> — нічого про вас. Голос без входу лише змінює лічильник жарту; хто голосував,
                сайт не знає. Ваш браузер пам’ятає ваші голоси, тему й мову у власному сховищі (localStorage); тема й мова
                до нас не потрапляють.</li>
              <li><strong>Коли ви входите</strong> через GitHub, Google, Microsoft, Telegram чи Facebook (з тих, що
                пропонує сайт), сервіс повідомляє номер вашого акаунта. Ми його не зберігаємо: з нього обчислюється код
                (HMAC-SHA256 із секретним ключем). Без ключа з коду не дізнатися акаунт. Поки що код живе лише в cookie
                входу; коли голосувати можна буде тільки після входу, ваші голоси зберігатимуться з цим кодом. Ми просимо
                в сервісу якнайменше даних і не зберігаємо ні імені, ні пошти, ні фото, ні профілю, ні токенів
                сервісу.</li>
              <li><strong>Навіщо:</strong> щоб кожен акаунт мав один голос на жарт. Підстава — надання послуги, про яку
                ви просите, входячи (ст. 6(1)(b) GDPR).</li>
              <li><strong>Як довго:</strong> доки ви їх не видалите. Кнопка «Видалити мої голоси» з’явиться в меню
                акаунта.</li>
            </ul>

            <h3>Cookies</h3>
            <ul>
              <li><strong>Cookie входу</strong> (<code>__Host-lazydad</code>) з’являється лише після входу. Він
                зашифрований і містить тільки код і назву сервісу. Зникає, коли ви закриваєте браузер або виходите.
                Лише якщо ви позначите «Не виходити 90 днів», він живе 90 днів і подовжується, поки ви користуєтеся
                сайтом.</li>
              <li>Під час входу сайт ставить короткі cookies, щоб перевірити, що відповідь сервісу стосується саме
                цього входу. Вони зникають, щойно вхід завершено (або за 15 хвилин).</li>
              <li>Аналітичних, рекламних чи стежувальних cookies немає, тому й банера про cookies немає.</li>
            </ul>

            <h3>Ваші права</h3>
            <p>Ваші голоси видно на сторінці, і ви зможете видалити їх будь-коли. Сайт не може пов’язати код із вами,
              тож запит листом ми не зможемо зіставити з вашими голосами: видалення працює лише з вашого входу. Ви
              також можете поскаржитися до органу із захисту даних там, де живете.</p>

            <h3>Сервіси, через які проходять дані</h3>
            <ul>
              <li><strong>Microsoft Azure</strong> (ЄС: Нідерланди та Швеція) — сервери й база даних.</li>
              <li><strong>Cloudflare</strong> — захищає сайт і передає до нього запити. Як і будь-який вебсервер, він
                бачить вашу IP-адресу, щоб доставити сторінку.</li>
              <li><strong>Grafana Cloud</strong> (ЄС) — технічні дані сайту: швидкість, помилки. IP-адреси й дані
                браузера звідти вилучено.</li>
              <li><strong>Сервіс, яким ви входите</strong> — на його боці діє його власна політика
                конфіденційності.</li>
            </ul>
            <p>Сам LazyDad ніде не записує IP-адрес. Щоб обмежити, як часто можна голосувати, сервер тримає адресу в
              пам’яті одну хвилину, а тоді забуває. Жарти пишуть моделі ШІ (Azure OpenAI); вони нічого не дізнаються про вас.</p>
          </div>
          <div class="ld-doc-body" lang="en">
            <h2>Privacy</h2>
            <p class="ld-doc-meta">Updated <time datetime="{{Updated}}">8 October 2026</time></p>
            <p>In short: LazyDad keeps as little as it can. To count one vote per joke for each account, it uses only a
              code worked out from the account: no name, email or photo. No analytics or advertising.</p>

            <h3>Who runs the site</h3>
            <p>LazyDad (lazydad.fyi) is a hobby project of a private individual, <a href="{{Owner}}">mykolad</a>. You can
              get in touch on GitHub: <a href="{{Repository}}/issues">open an issue</a>, or write through
              <a href="{{Owner}}">mykolad’s profile</a>.</p>

            <h3>What’s stored, and why</h3>
            <ul>
              <li><strong>Without signing in,</strong> nothing about you. A vote without signing in only changes the
                joke’s count; the site doesn’t know who cast it. Your browser remembers your votes, theme and language in
                its own storage (localStorage); the theme and language never reach us.</li>
              <li><strong>When you sign in</strong> with GitHub, Google, Microsoft, Telegram or Facebook (whichever the
                site offers), the provider tells us your account’s number. We don’t store it: we work out a code from it
                (HMAC-SHA256 with a secret key). Without the key, the code can’t be traced back to the account. For now
                the code lives only in your sign-in cookie; once voting needs signing in, your votes will be stored with
                it. We ask the provider for as little as it allows, and keep no name, email, photo, profile, or the
                provider’s tokens.</li>
              <li><strong>Why:</strong> so that each account has one vote per joke. The basis is providing the service
                you ask for by signing in (GDPR Article 6(1)(b)).</li>
              <li><strong>For how long:</strong> until you delete them. A “Delete my votes” button is coming to the
                account menu.</li>
            </ul>

            <h3>Cookies</h3>
            <ul>
              <li><strong>The sign-in cookie</strong> (<code>__Host-lazydad</code>) is set only once you sign in. It’s
                encrypted and holds only the code and the provider’s name. It goes when you close the browser or sign out.
                Only if you tick “Keep me signed in for 90 days” does it last 90 days, renewed while you use the
                site.</li>
              <li>While you sign in, the site sets short-lived cookies to check that the provider’s answer belongs to
                this sign-in. They go as soon as it’s done (or after 15 minutes).</li>
              <li>There are no analytics, advertising or tracking cookies, which is why there’s no cookie banner.</li>
            </ul>

            <h3>Your rights</h3>
            <p>Your votes are shown on the page, and you’ll be able to delete them at any time. The site can’t link the
              code to you, so a request by email couldn’t be matched to your votes: deleting works only while you’re
              signed in. You can also complain to the data protection authority where you live.</p>

            <h3>Services your data passes through</h3>
            <ul>
              <li><strong>Microsoft Azure</strong> (EU: the Netherlands and Sweden): the servers and the database.</li>
              <li><strong>Cloudflare</strong> protects the site and passes requests on to it. Like any web server, it sees
                your IP address to deliver the page.</li>
              <li><strong>Grafana Cloud</strong> (EU): the site’s technical data, such as speed and errors. IP addresses
                and browser details are removed from it.</li>
              <li><strong>The provider you sign in with:</strong> its own privacy policy applies on its side.</li>
            </ul>
            <p>LazyDad itself never writes IP addresses down. To limit how often one can vote, the server holds the
              address in memory for one minute, then forgets it. AI models (Azure OpenAI) write the jokes; they learn nothing about you.</p>
          </div>
        </article>
        """;
}
