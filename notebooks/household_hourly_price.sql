-- One row per metered hour: the meter, the EV charger's share, the household (meter - EV,
-- never below zero) and that hour's price. All times are UTC, stamped at the hour start.
-- Zaptec stamps its readings at the END of the interval, so they are moved 1 s back and
-- down to the hour. Used by price_timing.ipynb.
CREATE VIEW electricity_db.household_hourly_price AS
SELECT
  m.mrid                                              AS metering_point,
  m.dt_measured_utc                                   AS hour_utc,
  m.quantity                                          AS meter_kwh,
  COALESCE(z.ev_kwh, 0)                               AS ev_kwh,
  GREATEST(m.quantity - COALESCE(z.ev_kwh, 0), 0)     AS household_kwh,
  p.total                                             AS price_kr_incl_vat,
  p.value                                             AS price_kr_ex_vat,
  s.total                                             AS spot_kr_incl_vat
FROM eloverblik_energy_readings m
JOIN stromligning_price_data p
  ON p.price_type = 'price' AND p.date_time_utc = m.dt_measured_utc
LEFT JOIN stromligning_price_data s
  ON s.price_type = 'electricity' AND s.date_time_utc = m.dt_measured_utc
LEFT JOIN (
  SELECT TIMESTAMP(DATE_FORMAT(charge_date_time_utc - INTERVAL 1 SECOND, '%Y-%m-%d %H:00:00')) AS hour_utc,
         SUM(energy) AS ev_kwh
  FROM zaptec_charging_sessions
  GROUP BY 1
) z ON z.hour_utc = m.dt_measured_utc;
