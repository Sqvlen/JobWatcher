# Джерело LinkedIn (не підключено)

Адаптера **немає**. Рішення Задачі 13: законного способу читати вакансії LinkedIn без входу в акаунт замовника і без скрапінгу я не знайшов, тож джерело не підключено. Налаштування `JobWatcher:Sources:LinkedIn:Enabled` у `appsettings.json` типово `false`. Якщо його ввімкнути, у `ServiceCollectionExtensions.cs` реєструється `DisabledSource(VacancySource.LinkedIn, …)`: кожен цикл опитування повідомляє «не підключено», а користувач один раз отримує в Telegram повідомлення «ℹ️ Джерело LinkedIn не підключено: …» з причиною, що веде сюди. Налаштування ніколи не буває мовчазною пустушкою.

Це висновок із прочитаних документів, а не юридична консультація. Нижче сказано, що саме прочитано, що там написано і чого я не міг перевірити.

## Як читалося

- Усі документи спершу відкривалися лише інструментом читання вебсторінок, 2026-10-22 (UTC), без входу в акаунт, лише юридичні, довідкові й розробницькі сторінки LinkedIn та Microsoft Learn, де LinkedIn публікує документацію API. **Сторінок пошуку вакансій і сторінок окремих вакансій LinkedIn я не відкривав**, неофіційних адрес не пробував, нічого не обходив. Три пошукові запити йшли через вебпошук; серед результатів траплялися адреси сторінок вакансій LinkedIn (наприклад `linkedin.com/jobs/rss-feed-jobs`) і сторонні статті про RSS, їх я не відкривав.
- Інструмент віддає сторінку, стиснуту невеликою моделлю, тому цитати нижче (англійською, як в оригіналі) наведено так, як він їх повернув, і з сирим текстом сторінок символ у символ я їх не звіряв. Назви розділів і номери пунктів теж взято з його відповідей. Винятки описано в наступному пункті.
- Два документи перечитано напряму, без посередника, при виправленні цього файлу (2026-10-21 23:50 UTC): `robots.txt` (повністю, 120 190 байт, 4862 рядки) і Угоду користувача. Кожен був одним звичайним `GET` без входу в акаунт, з User-Agent `JobWatcher/1.0 (personal job alerts; documentation check of public robots.txt and legal pages)`, відповідь збережено поза проєктом і після звірення видалено. Із сирим текстом Угоди звірено п. 1.2 і пункти 2, 3, 4 та 13 розділу 8.2: їхні цитати нижче збігаються з ним слово в слово (у п. 4 нижче опущено закінчення «(such as LinkedIn for content it owns)»). Решту цитат цим не перевірено.
- Адреса `https://learn.microsoft.com/en-us/linkedin/talent/job-postings/` дала 404 (адреса вгадана мною; це не висновок про LinkedIn).

| Документ | Адреса | Версія / дата |
|---|---|---|
| Угода користувача LinkedIn | https://www.linkedin.com/legal/user-agreement | чинна з 3 листопада 2025 |
| robots.txt | https://www.linkedin.com/robots.txt | прочитано повністю 2026-10-21 23:50 UTC (заголовок `Last-Modified: Wed, 16 Sep 2026 17:03:01 GMT`) |
| Умови для краулерів (Crawling Terms and Conditions) | https://www.linkedin.com/legal/crawling-terms | остання редакція 25 травня 2017 |
| Довідка: заборонене ПЗ і розширення | https://www.linkedin.com/help/linkedin/answer/a1341387 | «оновлено 2 роки тому» |
| Довідка: сповіщення про вакансії (Job alerts) | https://www.linkedin.com/help/linkedin/answer/a511279 | «оновлено 6 місяців тому» |
| Умови використання API (API Terms of Use) | https://www.linkedin.com/legal/l/api-terms-of-use | остання редакція 13 грудня 2022 |
| Умови Job Posting API | https://www.linkedin.com/legal/l/job-posting-api-terms | 18 грудня 2023 |
| Документація: «Getting Access to LinkedIn APIs» | https://learn.microsoft.com/en-us/linkedin/shared/authentication/getting-access | `ms.date` 2025-06-24 |
| Документація: головна сторінка API LinkedIn | https://learn.microsoft.com/en-us/linkedin/ | `ms.date` 2025-07-30 |
| Документація: Job Posting API Overview | https://learn.microsoft.com/en-us/linkedin/talent/job-postings/api/overview?view=li-lts-2026-04 | `ms.date` 2025-06-09 |
| Документація: Job Posts using XML Feeds | https://learn.microsoft.com/en-us/linkedin/talent/job-postings/xml-feeds?view=li-lts-2025-10 | `ms.date` 2021-07-27 |
| Портал розробників: каталог продуктів Talent | https://developer.linkedin.com/product-catalog/talent | без дати |

## Що сказано в умовах

**Угода користувача, розділ 8 «LinkedIn "Dos and Don'ts"», пункт 8.2 «Don'ts»** (номери пунктів як у відповіді інструмента):

- Пункт 2: «Develop, support or use software, devices, scripts, robots or any other means or processes (such as crawlers, browser plugins and add-ons or any other technology) to scrape or copy the Services, including profiles and other data from the Services».
- Пункт 3: «Override any security feature or bypass or circumvent any access controls or use limits of the Services (such as search results, profiles, or videos)».
- Пункт 4: «Copy, use, display or distribute any information (including content) obtained from the Services, whether directly or through third parties (such as search tools or data aggregators or brokers), without the consent of the content owner».
- Пункт 13: «Use bots or other unauthorized automated methods to access the Services, add or download contacts, send or redirect messages, ...».
- Наслідок: «LinkedIn reserves the right to restrict, suspend, or terminate your account if you breach this Contract or the law or are misusing the Services.»
- Окремого розділу про API, RSS-стрічки чи платформу в Угоді інструмент не знайшов.

**robots.txt** (прочитано повністю, див. «Як читалося»). Початок файлу (рядки-коментарі): «The use of robots or other automated means to access LinkedIn without the express permission of LinkedIn is strictly prohibited. See https://www.linkedin.com/legal/user-agreement. LinkedIn may, in its discretion, permit certain automated access to certain LinkedIn pages, for the limited purpose of including content in approved publicly available search engines.» Далі в заголовку: «If you would like to apply for permission to crawl LinkedIn, please email whitelist-crawl@linkedin.com.» і «Any and all permitted crawling of LinkedIn is subject to LinkedIn's Crawling Terms and Conditions.» Потім ідуть блоки для названих роботів: `LinkedInBot` (`Allow: /`); блоки пошукових роботів (Googlebot, Bingbot та інші), де закрито, зокрема, `/jobs?runSearch*`, `/jobs/view/externalApply/`, `/jobs-guest/`, `/api/jobPostings/jobs*`, `/jsearch*`, `/job-apply/` (так у 41 блоці); блоки з `Disallow: /` для решти названих роботів (серед них ClaudeBot, GPTBot, Scrapy, Bytespider). **Останній блок файлу це `User-agent: *` із `Disallow: /`**, тобто будь-якому роботу, якого не названо вище, закрито все. Під ним коментар: «If you would like to crawl LinkedIn, please email whitelist-crawl@linkedin.com to apply for white listing.» Нашого бота (`JobWatcher/1.0`) серед названих немає, тож до нього застосовується саме цей блок: за robots.txt LinkedIn для нього закрито повністю, а єдиний шлях, який файл називає, це заявка на білий список за цією адресою (заголовок згадує такий дозвіл лише для включення вмісту в схвалені публічні пошукові системи). Такого дозволу в нас немає, і я його не просив. Висновок не залежить від robots.txt: Угода вже забороняє автоматичний доступ без дозволу LinkedIn, і robots.txt дає той самий результат.

**Угода користувача, розділ 1.2 «Members and Visitors»** (текст прочитано напряму, див. «Як читалося»): «This Contract applies to Members and Visitors.» і далі: «When you register and join the LinkedIn Services, you become a “Member”. If you have chosen not to register for our Services, you may access certain features as a “Visitor.”» Тобто Угода, а з нею і заборони розділу 8.2, діє не лише для зареєстрованих, а й для відвідувачів без акаунта: автоматичний доступ без входу в акаунт теж нею охоплено, і «читати без акаунта» заборон не обходить. Висновок від цього не змінюється.

**Crawling Terms and Conditions** (умови для краулерів пошукових систем, 2017): «Automated Crawling & Indexing without the express permission of LinkedIn is strictly prohibited.»; «Your use of data...will be confined solely to search indexing for display in a publicly available search engine.»; «LinkedIn may revoke any permission granted to you at anytime for any reason». Нас це стосується лише тим, що дозволу на автоматичне збирання немає, а єдине передбачене виключення (індексація для публічної пошукової системи) не для нас.

**Довідка «Prohibited software and extensions»**: LinkedIn забороняє «any third party software, including "crawlers", bots, browser plug-ins, or browser extensions that scrape, modify the appearance of, or automate activity on LinkedIn's website»; користувачі таких інструментів «risk having their accounts restricted or shut down».

**API Terms of Use**: заборонено «Access, store, display, or facilitate the transfer of any LinkedIn content obtained through the following methods: scraping, crawling, spidering or using any other technology or software to access LinkedIn content outside the APIs» і «capture, copy, cache, or store any Content ... except to the extent expressly permitted by these Terms».

## Які шляхи існують

| Шлях | Що це | Що потрібно | Придатний тут? |
|---|---|---|---|
| Офіційний API пошуку чи читання вакансій | У документації API розділи: Consumer, Compliance, Learning, Marketing, Sales, Talent Solutions. У жодному не знайдено продукту, що шукає чи читає чужі вакансії. | не існує | **Ні** |
| Відкриті дозволи (self-service) | Три дозволи доступні всім розробникам без схвалення: `profile` і `email` («Sign in with LinkedIn»), `w_member_social` («Share on LinkedIn»). Сторінка «Getting Access»: «Open Permissions are the only permissions that are available to all developers without special approval.» | реєстрація застосунку | **Ні**: вакансій вони не читають |
| Job Posting API | Розміщення вакансій у LinkedIn від імені клієнтів (ATS, дистриб'ютори вакансій). Напрямок «до LinkedIn», не з нього. «We are currently not accepting new partnerships for LinkedIn's Job Posting API.»; «The use of these APIs is restricted to those developers approved by LinkedIn.»; умови програми: «THE LINKEDIN JOB POSTING API PROGRAM IS A VETTED API PROGRAM AND YOU HAVE NO RIGHT TO USE ANY API OR DATA MADE AVAILABLE AS PART OF THIS PROGRAM UNLESS APPROVED BY LINKEDIN» | схвалення LinkedIn, договір, статус партнера Talent Solutions | **Ні** |
| Інші продукти Talent (Apply Connect, Apply with LinkedIn, Recruiter System Connect) | Судячи з описів у каталозі («Help candidates apply for your jobs on LinkedIn», «Sync your ATS with LinkedIn Recruiter»), це інтеграції для ATS і роботодавців. Партнерські програми із заявкою до LinkedIn («Most permissions and partner programs require explicit approval from LinkedIn»). CRM Connect, який теж згадано на головній сторінці документації, я не читав. | заявка партнера | **Ні**: це не читання вакансій |
| XML-стрічка вакансій (Basic Jobs) | Роботодавець чи ATS **віддає** свої вакансії LinkedIn стрічкою; LinkedIn їх завантажує. Напрямок знову «до LinkedIn». «The integration of the XML feed is limited to those developers approved by LinkedIn.» | схвалення, угода | **Ні** |
| Публічна RSS/Atom-стрічка пошуку вакансій | В офіційній документації, яку я читав, **немає**: у довідці про Job alerts названо лише e-mail і сповіщення в застосунку, про RSS чи експорт ані слова. Сторонні статті про «RSS для пошуку роботи на LinkedIn» я не відкривав: це не документація LinkedIn. | не існує (за документацією) | **Ні** |
| Сторінки LinkedIn (пошук і перегляд вакансій, `/jobs-guest/`, `/api/jobPostings/…`) | Те, що відкриває сайт у браузері. Читати це програмою означає скрапінг і автоматичний доступ: Угода 8.2, пункти 2, 3, 13, заголовок robots.txt. | нічого дозволеного немає | **Ні**, не використовувалось і не пробувалось |
| Сповіщення про вакансії (Job alerts) в акаунті замовника | Офіційна функція LinkedIn: «You can choose whether you want to receive these alerts on a daily or weekly basis through email, app notifications, or both.» Не більше 20 сповіщень («The max number of job alerts a member can have at once is 20.»). Налаштовується в акаунті, тож потрібен вхід замовника. | акаунт замовника; для роботи програми ще й доступ до його пошти | Лише як **варіант для замовника**, див. нижче |

## Варіант, який може вибрати замовник (поза межами цього проєкту)

Замовник може сам створити Job alerts у своєму LinkedIn (пошук за ключовим словом і містом, щоденні чи щотижневі листи). Це офіційна функція LinkedIn для власного акаунту, і вона не потребує від JobWatcher нічого. Автоматично передавати такі листи в Telegram можна було б лише окремою інтеграцією: JobWatcher мав би читати скриньку замовника (IMAP, Gmail API чи пересилання листів на окрему адресу) і розбирати HTML листів LinkedIn. Це нова інтеграція з доступом до особистої пошти, поза обсягом Задачі 13, і я її не будував. Також не знаю: чи документовано формат цих листів (я не бачив опису: розбір спирався б на верстку, яку LinkedIn може змінити будь-коли); чи є в Умовах LinkedIn додаткові обмеження на автоматичну обробку власних листів (в уривках, які я бачив, про це нічого немає, але Умов повністю я не читав). Вирішувати це замовнику. Поки він не вирішить, джерело лишається «не підключено».

## Висновок

Правило, за яким прийнято рішення: законний шлях є лише тоді, коли LinkedIn сама публікує стрічку чи API, яким можна користуватися без особливого партнерства і без входу замовника, і її умови це не забороняють. **Не виконано жодної частини**:

- Відкритого API для читання вакансій немає: усі продукти, що стосуються вакансій, спрямовані «до LinkedIn» і доступні лише схваленим партнерам (Job Posting зараз не приймає нових партнерів).
- Публічної RSS-стрічки вакансій у документації немає.
- Угода користувача прямо забороняє скрапінг і автоматичні засоби доступу, а також обхід обмежень пошуку (пункти 2, 3 і 13 розділу 8.2). Вона діє і для відвідувачів без акаунта (розділ 1.2), тож читання без входу цього не обходить. robots.txt для не названих роботів теж закриває все (`User-agent: *` / `Disallow: /`).
- Єдина офіційна функція, що відповідає потребі (Job alerts), прив'язана до акаунту й пошти замовника.

Тому гілка 2б: `DisabledSource(VacancySource.LinkedIn, …)`, типово вимкнено.

**Впевненість.** Висока, що відкритого офіційного API читання вакансій немає (однаково в чотирьох місцях документації: головна сторінка API, «Getting Access», каталог Talent, огляд Job Posting) і що Угода забороняє скрапінг (це написано прямо) та поширюється на відвідувачів без акаунта (розділ 1.2, текст прочитано напряму). Помірна, що офіційної RSS-стрічки немає: це відсутність у прочитаній документації, а не доказ відсутності. Правило вимагає «якщо сумніви, то шляху немає», тож це не змінює рішення.

## Чого я не міг перевірити

- Угоду користувача із сирим текстом звірено лише в частині (розділ 1.2 і пункти 2, 3, 4, 13 розділу 8.2, див. «Як читалося»); повністю її не читав. Умови LinkedIn Jobs (https://www.linkedin.com/legal/jobs-terms-conditions) не читав.
- Цитати: див. «Як читалося» (їх подано з відповідей інструмента, без звіряння із сирим текстом, окрім robots.txt, розділу 1.2 і чотирьох пунктів розділу 8.2, названих там).
- Не входив в акаунт, тому не знаю, чи дають платні акаунти (Premium, Recruiter) якісь функції експорту чи інтеграції. Не знаю про можливі приватні домовленості LinkedIn про доступ до даних (їх у відкритій документації немає). Не писав у LinkedIn. Розробницьких документів, що вимагають входу, не бачив.
- Документація й умови змінюються: усе вище знято 2026-10-22 (robots.txt і Угоду перечитано напряму 2026-10-21 23:50 UTC).

## Як це працює в програмі

- `Sources:LinkedIn:Enabled = false` (типово): нічого не реєструється, жодних повідомлень.
- `JobWatcher__Sources__LinkedIn__Enabled=true`: реєструється `DisabledSource(VacancySource.LinkedIn, причина)`. `WatchCycle` ловить `SourceNotConnectedException`, пише в журнал рівня Information і один раз за запуск програми надсилає в Telegram «ℹ️ Джерело LinkedIn не підключено: …» (стан у пам'яті: після перезапуску повідомлення прийде знову один раз; якщо Telegram не прийняв повідомлення, воно пропонується знову в наступному циклі). Інші джерела працюють як звичайно.
- Валідатор налаштувань вважає LinkedIn увімкненим джерелом: конфігурація, де увімкнено лише його, проходить перевірку, але програма тоді нічого не шукає, лише надсилає це одне повідомлення.
- Причина в повідомленні (`ServiceCollectionExtensions.LinkedInNotConnectedReason`): «LinkedIn не пропонує дозволеного доступу до вакансій (відкритого API чи стрічки немає, а умови забороняють автоматичний збір). Подробиці: docs/sources/linkedin.md». Якщо цей висновок зміниться (LinkedIn опублікує відкрите API чи стрічку, або замовник вирішить підключити пошту), замінити реєстрацію на справжній адаптер за зразком `DjinniSource` + `RssFeed`.

## Тести

`tests/JobWatcher.Tests/CompositionTests.cs`: `LinkedIn_enabled_without_access_registers_DisabledSource`, `LinkedIn_disabled_is_not_registered`, `LinkedIn_enabled_makes_the_watch_cycle_send_the_not_connected_notice_once`, а також `Only_enabled_sources_are_registered` (рядки з LinkedIn) і `Disabled_sources_are_not_registered`. Тести ніколи не звертаються до мережі; фікстури для LinkedIn немає, бо читати нічого.
